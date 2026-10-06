# Plan — REC-1 / REC-2: dueño de plataforma en Recepción

Plan para `docs/platform-owner.md`. No ejecuté nada: lo que sigue sale de leer el código, y los supuestos sin comprobar van marcados.

## 0. Lo que el código obliga

- **Compuerta única.** Toda ruta `/api/**` pasa por un middleware inline en `backend/Program.cs` (l. 58–72): 401 si es anónimo, luego `Identity.Bind`, 403 si falla. Los `MapGroup` solo añaden `.RequireAuthorization()`; no hay filtros de endpoint.
- **El middleware puede leer el endpoint.** `WebApplication` inserta `UseRouting` al inicio, así que `ctx.GetEndpoint()` ya está resuelto ahí (supuesto no ejecutado). Si fuera `null`, todo acto de plataforma daría 403: falla cerrado y se ve en el primer curl.
- **Metadata + compuerta en ese middleware** es el diseño correcto. Un filtro global no cubriría el fallback 404.
- **Hechos del refinador confirmados:**
  - `Identity.Bind` devuelve `false` sin `tenant_id` (l. 32).
  - `Member.Subject` es único global (`Models.cs` l. 213).
  - `CrmEndpoints.Audit` escribe `Actor = u.Subject`, y `Audit.Actor` es `text`, así que no hay migración.
  - `CrmDb.SaveChangesAsync` lanza si `scope.Id` es vacío o distinto del `TenantId` de la fila.
- **Arnés de pruebas.** No hay `WebApplicationFactory` ni `TestHost`. Las pruebas llaman a `Identity.Bind` con `DefaultHttpContext` (`HospitalClinicalTests.cs`) y a los handlers estáticos (`InstallationTests`). `SecurityTests.cs` es hoy puro, sin base.
- **La API no mira `azp`.** Acepta cualquier token con audiencia `hospital-api`; `tests/keycloak_dev.py` saca los suyos por el direct grant de `hospital-web`. El tope de `recepcion-web` (AC 1) solo gobierna el login por navegador.
- **Frontend.** next-auth 4 con estrategia JWT (`frontend/src/lib/auth.ts`); los tokens viven en la cookie de sesión de next-auth. `crm-proxy.ts` solo reenvía `content-type` e `idempotency-key`, así que una `X-Acting-Tenant` del navegador ya no pasa.
- **Hallazgo: la bandeja se abriría por el lado Next.** `frontend/src/lib/inbox-auth.ts` autoriza `/api/conversations`, `/api/messages` y `/api/kapso/stream` con un `GET /api/crm/channels`, y luego lee Kapso con la `KAPSO_API_KEY` del servidor web. Si `GET /api/channels` es operable y el proxy pone la cabecera, el dueño abre la bandeja sin que el barrido del backend lo vea. Paso F3 lo cierra.

## 1. Contrato fijo backend / frontend

| Qué | Valor |
|---|---|
| Rol | `platform-owner` (en `realm_access.roles`) |
| Cabecera | `X-Acting-Tenant: <uuid>`, exactamente un valor; nunca cuerpo ni query |
| `CurrentUser` de plataforma | `Role = "platform"`, `Subject = "platform:<sub>"`, `Name` = claim `name` o «Dueño de plataforma» |
| Cookie de la elección | `recepcion.acting` (`__Secure-recepcion.acting` bajo https); HttpOnly, SameSite=Lax, Path=/, maxAge 3600 |
| Contenido de la cookie | JWE de `{ sub, tenantId, name }` con `encode`/`decode` de `next-auth/jwt` (ya importado en el proxy), `AUTH_SECRET`, `salt: 'recepcion.acting'` |

**`GET /api/platform/tenants`** — sin cabecera.

```json
200 { "tenants": [ { "id": "uuid", "name": "string", "hospitalConfigured": true, "whatsAppConnected": false } ] }
```

- Orden: `name`, luego `id`. Otro rol → 403.
- `hospitalConfigured` es `HospitalClient.IsConfigured(id)`: configurado, no comprobado. Con los valores por defecto de instalación será `true` para todas; la comprobación real es `POST /connection/check` al elegir. Por eso no lo llamo «conectado».
- `whatsAppConnected`: existe un `Channel` del inquilino con `PhoneNumberId != "demo"` (`IgnoreQueryFilters`).

**Respuestas de la compuerta**, en este orden (espejo de Hospital):

| Caso | Respuesta |
|---|---|
| `platform-owner` + claim `tenant_id` | 401 |
| Cabecera presente y token no de plataforma | 403 |
| Plataforma + endpoint sin marca, o sin endpoint | 403 |
| Plataforma + endpoint que pide recepción + sin cabecera | 403 |
| Plataforma + cabecera no-UUID, vacía, repetida o sin fila en `Tenants` | 404 |
| Válido | `scope.Id` = recepción; `CurrentUser` como arriba |

La negativa por marca va antes de la consulta a `Tenants`, así el barrido no toca la base.

**Sesión web** (aditiva): `session.platform?: { actingFor: { tenantId: string; name: string } | null }`. Ausente significa sesión de hospital.

**Rutas Next nuevas:** `POST /api/platform/acting` con `{ tenantId }` → 204 y cookie; `DELETE` → la borra. La lista se lee por `/api/crm/platform/tenants` (`platform` ya está en `ALLOWED`).

## 2. Diseño

**Fichero nuevo `backend/PlatformOwner.cs`**, uno solo:

- `Identity.Roles(ClaimsPrincipal)`: se extrae el parseo de `realm_access` que hoy vive dentro de `MapRole`, para no duplicarlo.
- `PlatformOwner.Is(user)` → `Roles(user).Contains("platform-owner")`.
- `MapRole`: primera línea tras parsear, `if (roles.Contains("platform-owner")) return null;` (INV-R3, aunque también lleve `Administrador`).
- Marca `sealed record PlatformOperable(bool NeedsTenant = true)`, puesta con `.WithMetadata(...)` en cada ruta operable.
- `static Task<int?> Gate(ctx, db, scope, current)`: `null` = no es plataforma ni trae cabecera (sigue a `Identity.Bind`); `0` = admitido; otro valor = status.
- `static Tenants(CurrentUser u, CrmDb db, HospitalClient h)`: handler de la lista, llamable directo desde pruebas como `InstallationEndpoints.Read`.

**`Program.cs`:** la llamada a `Gate` va delante de `Identity.Bind`. Con `0` salta `Bind` entero, así no hay `Member`, ni `Tenant`, ni `HospitalIdentitySync` de primer ingreso (INV-R1).

**Segunda capa por ruta.** `CurrentUser` gana `Platform => Role == "platform"` y `RequireAdminOrPlatform()`. Solo los siete handlers operables que hoy llaman `RequireAdmin()` cambian a ese. `Admin` sigue `false` para plataforma, así que una ruta marcada por error que conserve `RequireAdmin()` sigue negando.

**Auditoría.** `CrmEndpoints.Audit` no se toca: `u.Subject` ya es `platform:<sub>` y `t.Id` la recepción objetivo.

**Para el barrido.** Mover la línea de `Map*()` de `Program.cs` a `Hosting.MapRecepcionApi(this WebApplication)` y el bloque de registros (l. 16–41) a `Hosting.AddRecepcionServices(...)`. Es mover, no reescribir. La prueba construye el `WebApplication` sin arrancarlo y enumera `DataSources`. Los servicios hacen falta porque la inferencia de parámetros de minimal API los consulta al construir cada endpoint.

## 3. Rutas

**Operables**

| Entrega | Método y path | Fichero | Cabecera |
|---|---|---|---|
| REC-1 | `GET /api/platform/tenants` (nueva) | `PlatformOwner.cs` | no |
| REC-1 | `GET /api/hospital/connection` | `HospitalConnectionEndpoints.cs` | sí |
| REC-2 | `GET /api/platform/installation` | `InstallationEndpoints.cs` | sí |
| REC-2 | `GET /api/hospital/connection/setup` | `HospitalConnectionEndpoints.cs` | sí |
| REC-2 | `PUT /api/hospital/connection` | ídem | sí |
| REC-2 | `POST /api/hospital/connection/check` | ídem | sí |
| REC-2 | `GET /api/channels` | `WhatsAppEndpoints.cs` | sí |
| REC-2 | `POST /api/channels/onboarding` | ídem | sí |
| REC-2 | `POST /api/channels/sync` | ídem | sí |
| REC-2 | `PATCH /api/channels/{id:guid}` | ídem | sí |
| REC-2 | `POST /api/channels/{id:guid}/diagnostics` | `ChannelDiagnostics.cs` | sí |

`GET /api/channels` no está en la lista literal del AC 10, pero sin él no hay ids para activar ni diagnosticar. Hay que añadirlo a la spec (hueco 1).

**Negadas (403), casos nominales del barrido**

- Agente y ajustes: `GET|PUT /api/settings` (AC 14: `agentEnabled` vive ahí), `POST /api/assistant`, `GET /api/agent-metrics`, `GET /api/jobs`.
- Número del llamador: `POST /api/channels` (anti-criterio: acepta `PhoneNumberId` del cuerpo).
- Bandeja: `GET /api/conversations`, `GET|POST /api/conversations/{id}/messages`, `POST .../media`, `GET /api/messages/{id}/media`, `PATCH /api/conversations/{id}`, `PATCH .../workflow`, `POST .../read`, `POST /api/conversations/assign`, `POST .../macros/{macroId}`, `/api/saved-replies`, `/api/inbox-views`, `/api/macros`.
- Contactos y pacientes: `/api/contacts/**`, `POST /api/contacts/{id}/patient`, `POST /api/hospital/contacts/{id}/patients/search` (AC 12).
- Clínicas y agenda: `/api/hospital/conversations/{id}/clinical/**`, `/api/hospital/agenda`, `/availability`, `/appointments`, `/api/hospital/contacts/{id}/appointments|prescriptions`.
- Resto: `/api/me`, `/api/overview`, `/api/members/**`, `/api/audit`, `/api/activities`, `/api/activity-feed`, `/api/opportunities`, `/api/companies`, `/api/commercial/**`, `/api/appointment-reminders/**`, `/api/google/**`.

`/webhooks/kapso`, `/oauth/google/callback` y `/health/*` quedan fuera de `/api` y no cambian.

## 4. Pasos

Pruebas con `scripts/test-backend.sh` (sin filtro `~Agent`). Las que tocan base van en `[Collection("Onboarding database")]`, que ya desactiva el paralelismo; así los conteos globales de `Tenants` y `Members` son estables.

### H — lado Hospital (worktree de Hospital, paso aparte)

- **H1 (AC 1).** Rojo: editar `Should_cap_the_scope_at_the_seven_PRD_roles_When_recepcion_web_is_reconciled` en `backend/tests/Architecture/Hospital.Architecture.Tests/RecepcionRealmScriptTests.cs` para exigir los siete más `platform-owner`. Verde: en `infra/keycloak/configure-realms.sh` §9b pasar esa lista ampliada a `kc_client_scope_roles_exactly` y corregir el `echo` y la `description` del cliente. `Should_name_no_clinician_role_or_Administrador_When_section_9_executes` debe seguir verde sin tocarla.
- §9c (cuentas de servicio) y `KC_PRD_REALM_ROLES` no se tocan. Actualizar la frase de `recepcion-identidad.spec.md`.

### B — backend

| Paso | AC | Rojo primero | Verde |
|---|---|---|---|
| B1 | 3 | Prueba nueva en `SecurityTests.cs` junto a `OnlyHospitalRolesMap`: `platform-owner` solo y `platform-owner` + `Administrador` dan `null` | `Identity.Roles` + línea en `MapRole` |
| B2 | 2, 5 | Pruebas nuevas en `SecurityTests.cs` (pasa a usar base) sobre `PlatformOwner.Gate`, con dos inquilinos sembrados: la tabla de §1 fila por fila; tras cada caso `Tenants` y `Members` no cambian | `PlatformOwner.cs` (marca + `Gate`), llamada en `Program.cs` |
| B3 | 4 | Prueba nueva `backend.Tests/PlatformOwnerTests.cs`: forma de la lista, orden, sin `HospitalConnection`, `KapsoCustomerId` ni `Guide` en el JSON; `admin`, `agent` y `doctor` → `AccessDeniedException` | `PlatformOwner.Tenants` + `MapGet` marcado `NeedsTenant: false` |
| B4 | 6, 7, 12, 14 | Prueba nueva en `PlatformOwnerTests.cs`: enumera los `RouteEndpoint` bajo `api/`; el conjunto marcado debe ser igual a la lista escrita de §3; por cada no marcado, `Gate` con ese endpoint → 403; casos nominales de AC 7, 12 y 14 con nombre | `Hosting.cs` (mover registros y mapeo), marcas en las 11 rutas |
| B5 | 10, 11 | Ampliar `WhatsAppOnboardingTests`: `SetupUsesOwnedCustomerSpanishExistingNumbersAndSafeRedirects`, `RepeatedSyncRegistersOnceAndDoesNotActivateAgentOrDuplicateWebhook`, `ProviderFilterIsNotTrustedForForeignCustomer` y `NumberCannotBeReassignedFromAnotherHospital` corren también con `Role = "platform"`; `NonAdminCannotStartOrSync` queda igual | `RequireAdminOrPlatform()` en `Start`, `Sync`, `PATCH /channels/{id}` y diagnóstico |
| B6 | 12 | Caso nuevo en `HospitalConnectionTests` para el actor de plataforma (la búsqueda de pacientes ya la niega B4) | `RequireAdminOrPlatform()` en `connection/setup` y `PUT /connection` |
| B7 | 13 | Prueba nueva en `PlatformOwnerTests.cs`: tras `Start` y `Sync` con actor de plataforma, las filas `Audit` llevan `TenantId` = objetivo y `Actor = "platform:<sub>"`; ningún `Member` con ese `sub` | Nada si B2 está bien; es la prueba que lo fija |
| B8 | 16 | Ampliar `InstallationTests`: `Role = "platform"` lee; `OnlyAdministratorsMayRead` sigue verde | `RequireAdminOrPlatform()` en `InstallationEndpoints.Read` |

Límite de B4: sin `TestHost` la prueba afirma la decisión de la compuerta por endpoint, no el status por HTTP. El status real lo cubre el curl de §6. Si la enumeración no construye, el AC 6 permite recorrer la lista escrita, y hay que decirlo en la prueba.

### F — frontend (`npm run test:server`, `npm run typecheck`, `npm run lint`)

- **F1 (AC 8) — sesión.** `lib/auth.ts`: en el callback `jwt`, `token.platform` = el access token lleva `platform-owner` (decodificar el payload; es pista de UI, la API manda). El callback `session` expone `platform`. Recalcular al refrescar en `crm-proxy.ts`.
- **F2 (AC 8) — cookie y cabecera.** Nuevo `lib/acting.ts` (server-only: leer, escribir y borrar; una cookie con otro `sub` se ignora). Nuevo `app/api/platform/acting/route.ts`: comprueba origen, exige `token.platform` y valida el `tenantId` contra `/api/platform/tenants` antes de sellar. `crm-proxy.ts` pone `X-Acting-Tenant` solo si `token.platform` y la cookie es de su `sub`.
  - Prueba nueva `server-tests/acting.test.ts`: sesión de hospital nunca envía la cabecera, ni con cookie sobrante; plataforma envía la elegida; cookie de otro `sub` se ignora; una cabecera del navegador no se reenvía.
- **F3 (decisión 2) — cerrar la bandeja.** `authorizeInbox` rechaza con 403 si `token.platform`, y su llamada interna a `proxy` no adjunta la cabecera (parámetro `acting: false`), así el backend niega también. `app/inbox/page.tsx` redirige a `/` en sesión de plataforma. Caso en `acting.test.ts`.
- **F4 (AC 8, 15) — vista.** `app/page.tsx`: si `session.platform`, renderiza un `components/platform-workspace.tsx` nuevo en vez de `Workspace`. `Workspace` no sirve: pide `/me` y `/overview`, ambas negadas. Sin `?view=`:
  - sin elección → selector (lista de `/platform/tenants`);
  - con elección → aviso «Actuando en nombre de ‹nombre›» con «Cambiar» (`DELETE` + `router.refresh()`), y dos secciones.
- **F5 (AC 15, 16) — piezas reutilizadas.**
  - `HospitalConnection` (`hospital-connection.tsx`): sirve con una prop `platform` que oculta `hospital-actions`, `hospital-next-steps`, «Usar otra cuenta» y la frase «Has entrado como…».
  - `useWhatsAppConnect(true, …)` + `WhatsAppLinkFlow` (`whatsapp-link-flow.tsx`): tal cual.
  - `ChannelConnection` (`conversation-workspace.tsx` l. 458): tal cual para el diagnóstico.
  - Fila de canal con Pausar/Habilitar: copiar el bloque de `SettingsView` (`workspace.tsx` l. ~2318–2350), que no es extraíble sin tocar `/settings`.
  - AC 16: los nombres de variable que faltan ya salen de `whatsappStep` en `lib/first-steps.ts`; si falta alguno se muestra como tarea del operador y no se pinta «Agregar número».
  - `app/whatsapp/page.tsx` y `whatsapp-connect.tsx`: en sesión de plataforma redirigir a `/` (usa `/me` y enlaza a la bandeja).
- **F6.** `/api/session/end` borra también `recepcion.acting`.
- **F7 (AC 8, 15).** Prueba Playwright nueva `tests/platform-owner.spec.ts`: selector al entrar, aviso, sin enlace a bandeja, `localStorage` y URL sin el id. Necesita un usuario de plataforma con contraseña definitiva en `tests/login.ts` (hueco 4).

### D — documentación (AC 9)

- `docs/deployment.md` l. 48 («sólo puede afirmar los siete roles») y l. 60 («No hay selector de empresas»), más una fila en la tabla de roles.
- `docs/hospital-integration.md` l. 7: la excepción de la cabecera para plataforma.
- `README.md` l. 54 («El tenant procede del JWT firmado»).

**Orden:** H1 → B1 → B2 → B3 → B4 → (B5, B6, B8 en cualquier orden) → B7 → F1 → F2 → F3 → F4 → F5 → F6 → F7 → D. El frontend puede empezar en F1–F3 contra el contrato de §1 mientras el backend va por B4.

## 5. Verificación en vivo

**Imágenes** (mismo patrón que `plt-api` / `plt-web` de Hospital), desde el worktree de Recepción:

- `docker build -t recepcion-api:plt backend`
- `docker build -t recepcion-web:plt frontend`

**API copia `plt-recepcion-api`** en `127.0.0.1:5216:8080`, redes `recepcion_default` y `hospital`:

- **Base propia.** `docker exec recepcion-db-1 createdb -U recepcion recepcion_plt`. No compartir la base viva: la copia arranca `AgentWorker` y los demás workers y reclamaría trabajos reales.
- **Volumen de llaves propio** (`plt-recepcion-keys:/keys`).
- **Env**, con `--env-file` del `.env` del repo principal más overrides: `ConnectionStrings__Database`, `KEY_DIRECTORY`, `INITIALIZE_DATABASE=true`, `SEND_ENABLED=false`, `REMINDERS_SEND_ENABLED=false`, `KAPSO_MANUAL_SEND_ENABLED=false`.
- **Del `.env` necesita:** `ASPNETCORE_ENVIRONMENT`, `PHONE_HASH_KEY`, `POSTGRES_PASSWORD`, `Auth__Authority`, `Auth__Audience`, `KEYCLOAK_INTERNAL_ISSUER`, `HOSPITAL_API_URL`, `HOSPITAL_PUBLIC_URL`, `HOSPITAL_SERVICE_CLIENT_SECRET`, `HOSPITAL_ALLOWED_API_ORIGINS`, `DEV_HOSPITAL_TENANT_ID`, `KAPSO_API_KEY`, `KAPSO_WEBHOOK_URL`, `KAPSO_WEBHOOK_SECRET`, `Kapso__Tenants__<uuid>__CustomerId`.

**Web copia `plt-recepcion-web`** en `127.0.0.1:3216:3000`, mismas redes:

- **Env:** `AUTH_SECRET`, `NEXTAUTH_SECRET`, `NEXTAUTH_URL=http://127.0.0.1:3216`, `API_URL=http://plt-recepcion-api:8080`, `KEYCLOAK_ISSUER`, `KEYCLOAK_INTERNAL_ISSUER`, `KEYCLOAK_CLIENT_ID`, `KEYCLOAK_CLIENT_SECRET`. Sin `KAPSO_*`.
- **Host `127.0.0.1`, no `localhost`.** Las cookies no distinguen puerto: en `localhost` la copia pisaría la sesión de `:3215`.

**Dos cosas que tocan el Keycloak local** (hueco 3):

1. Reejecutar el job `keycloak-config` desde el worktree de Hospital tras H1. Sin eso, el token de `recepcion-web` de la cuenta de plataforma no lleva el rol.
2. `recepcion-web` admite un solo redirect (`KC_RECEPCION_WEB_REDIRECT_URIS`). Hay que añadir a mano `http://127.0.0.1:3216/api/auth/callback/keycloak`, su web origin y el post-logout. El job lo revierte en su siguiente corrida, así que va después del punto 1.

**Recorrido:**

1. curl a `:5216` con un token real de plataforma (direct grant de `hospital-web`, como del lado Hospital) y otro de `dev-administrador-a`: la tabla de §1 caso por caso.
2. Barrido de las negadas nominales de §3 → 403.
3. `GET /api/platform/tenants` → A, B y la clínica local.
4. Con cabecera: `connection`, `connection/check`, `installation`, `channels`, `channels/sync`.
5. En `psql` sobre `recepcion_plt`: `Audits` con `Actor` `platform:…` y cero `Members` con ese `sub`.
6. Navegador en `http://127.0.0.1:3216`: selector → aviso → conexión → WhatsApp → «Cambiar»; `/inbox` y `/api/conversations` devuelven 403 o redirigen.

**Kapso en vivo.** `channels/onboarding` solo contra el inquilino que ya tiene `Kapso__Tenants__…__CustomerId`. En cualquier otro, `EnsureCustomer` crea un cliente real en el proyecto Kapso. La decisión 3 lo autoriza en producto, no como efecto de una prueba local.

## 6. Riesgos

- **Bandeja por el lado Next** (§0): sin F3, la decisión 2 se rompe aunque el backend esté verde.
- **Habilitar un número con el agente ya activo.** `PATCH /channels/{id}` con `enabled: true` en una recepción con `AgentEnabled = true` y `SEND_ENABLED = true` hace que el agente conteste en ese número. El AC 10 lo permite y el anti-criterio prohíbe «activar el envío automático en nombre»: chocan (hueco 2).
- **`connection/check` escribe sin auditar.** `HospitalIdentitySync` copia nombre y zona a `Tenant` y hoy no deja `Audit`. El AC 13 dice «cada acto» (hueco 5).
- **N+1 en la lista.** `IsConfigured` hace una consulta por inquilino. Vale para decenas de recepciones; con cientos, una sola consulta.
- **PHI.** `GET /api/channels` devuelve `Channel` entero (`KapsoCustomerId`, `LastWebhookAt`, `DoctorId`), lo mismo que ve el Administrador; no hay dato de paciente. Ninguna ruta operable lee `Contacts`, `Messages` ni `Conversations`.
- **Migraciones.** Ninguna; `Audit.Actor` es `text`. `recepcion_plt` es una base nueva, solo de la copia.
- **Proveedor externo.** Nada nuevo: `onboarding` y `sync` llaman a Kapso como hoy, ahora también con actor de plataforma (decisión 3). Las llamadas a Hospital siguen usando `recepcion-service-<tenant>`; el token del dueño no se reenvía.

## 7. Lo que NO se hace

- Formulario para `PUT /api/hospital/connection`. Hoy no existe en el frontend (la conexión sale de los valores de instalación); la ruta queda operable y probada, sin UI.
- `/api/me`, `/api/settings` ni `/api/audit` para plataforma.
- `Member` o `Tenant` para el dueño, mapeo a `admin`, migración.
- Paquetes nuevos (`Mvc.Testing`, `TestHost`), librería de sellado, usuario de plataforma en el seed de desarrollo.
- Teléfono de emergencia del inquilino, Google Calendar, recordatorios.

## 8. Huecos que te tocan

1. **`GET /api/channels` operable:** añadirlo a la lista de la spec, o dime otra fuente de ids de canal.
2. **Habilitar un número con el agente ya activo:** ¿se permite tal cual, o plataforma solo puede pausar cuando `AgentEnabled` es `true`? Lo segundo es una línea en el `PATCH`.
3. **Keycloak local:** ¿autorizas reejecutar el job y añadir a mano el redirect de `:3216`? Sin el redirect solo hay verificación por curl, no por navegador.
4. **Usuario de plataforma para Playwright:** la cuenta de `KC_PLATFORM_OWNERS` nace con contraseña temporal. F7 necesita una con contraseña definitiva, o se queda en recorrido manual.
5. **Auditar `connection/check` de plataforma:** propongo una fila `hospital.checked` solo cuando el actor es plataforma. Los GET no se auditan.
6. **`hospitalConfigured` en vez de «conectado»** en la lista: confirma el nombre, o acepta una comprobación en vivo por recepción (N llamadas a Hospital por carga).

### Critical Files for Implementation

- /private/tmp/claude-501/-Users-robertoflores2790-repos-converhub-Medical-Hospital/0960dd43-e99e-40dc-81c2-46b916392a6e/scratchpad/wt-recepcion/backend/Identity.cs
- /private/tmp/claude-501/-Users-robertoflores2790-repos-converhub-Medical-Hospital/0960dd43-e99e-40dc-81c2-46b916392a6e/scratchpad/wt-recepcion/backend/Program.cs
- /private/tmp/claude-501/-Users-robertoflores2790-repos-converhub-Medical-Hospital/0960dd43-e99e-40dc-81c2-46b916392a6e/scratchpad/wt-recepcion/backend/WhatsAppEndpoints.cs
- /private/tmp/claude-501/-Users-robertoflores2790-repos-converhub-Medical-Hospital/0960dd43-e99e-40dc-81c2-46b916392a6e/scratchpad/wt-recepcion/frontend/src/lib/crm-proxy.ts
- /private/tmp/claude-501/-Users-robertoflores2790-repos-converhub-Medical-Hospital/0960dd43-e99e-40dc-81c2-46b916392a6e/scratchpad/wt-recepcion/frontend/src/lib/inbox-auth.ts

## 9. Huecos resueltos (2026-10-06)

1. `GET /api/channels` es operable (sin él no hay ids de canal).
2. Con `AgentEnabled = true` en la recepción, plataforma solo puede **pausar** un número; habilitarlo responde 409. Con el agente apagado puede habilitar y pausar.
3. Verificación en vivo: se reejecuta el job del realm y se añade a mano el redirect de `:3216` en dev.
4. F7 (Playwright) no se escribe; el recorrido de navegador es manual.
5. `POST /api/hospital/connection/check` de un actor de plataforma escribe una fila `Audit` `hospital.checked`.
6. `hospitalConfigured` se queda con ese nombre.

## 10. Pasos saltados (frontend)

- **«Prueba de la vista» de AC 8 y AC 15: no hay prueba automática de la vista.** `frontend/` no tiene arnés de componentes (ni jsdom ni testing-library); sus únicas pruebas automáticas son `server-tests/` (lógica de servidor con `node --test`) y Playwright contra el stack real, y F7 quedó fuera por el hueco 4 (no hay usuario de plataforma con contraseña definitiva). Montar un arnés solo para esta vista se descartó.
- **Qué la cubre en su lugar.** La lógica que la vista no puede saltarse está en `server-tests/acting.test.ts`: cookie sellada y ligada al `sub`, cabecera puesta solo por el proxy, recepción pintada contra recepción sellada (`X-Acting-Expected` → 409 sin llamar a la API), bandeja cerrada. La composición (selector, aviso «Actuando en nombre de…», «Cambiar», secciones, 409 al habilitar, tarea del operador, cambio de recepción en otra pestaña, 1440 y 360 px) se verificó con un recorrido de navegador sin cabeza contra una API sintética y una sesión fabricada, no contra el backend ni Keycloak reales. Ese recorrido no queda en el repo: no es una regresión que corra sola.
- **Lo que eso deja sin cubrir.** Un cambio que rompa el render de la vista de plataforma no lo detecta ninguna prueba; lo detecta el recorrido manual de §5 paso 6. La recuperación de sesión en pestaña de plataforma (`session-recovery.tsx`) tampoco tiene prueba.
