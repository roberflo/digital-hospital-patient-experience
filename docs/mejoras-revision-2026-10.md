# Mejoras de la revisión 2026-10

Origen: revisión del 2026-10-17 (UX, deuda técnica, integración con Hospital), revalidada contra
`origin/main` (Recepción `7b4120c`, Hospital `423c5152`). Invariantes INV-1…INV-8: `docs/backlog.md`.
De 50 hallazgos: 19 vigentes, 20 vigentes en parte, 9 falsos, 2 ya resueltos (tabla al final).

## Decisiones del dueño (2026-10-17)

- **OQ-1 emergencias:** el número de emergencia sale siempre que el modelo falle (MR-1, MR-3). La
  lista ampliada de señales de alarma (MR-35) se propone aquí y **no se publica hasta que el dueño
  o el responsable clínico la apruebe**.
- **OQ-3 `/inbox`:** se conserva la vista; el envío pasa por la API de Recepción (MR-36).
- **OQ-4 llavero:** autorizado proteger el llavero y separar/cifrar su respaldo (MR-37).
- **OQ-5 secretos:** autorizado un secreto por `recepcion-service-<tenant>` (MR-38).
- **OQ-6 migraciones:** autorizadas en Recepción (MR-39, MR-40) y la de idempotencia en Hospital (MR-41).
- **S6 autoría:** las acciones del personal van a Hospital con el token del propio usuario; la cuenta
  de servicio queda solo para el agente (MR-11).
- **Sigue abierta OQ-2 (bloqueante, clínica/administrativa):** qué es «primera vez» frente a
  «subsecuente» (`AgentRuntime.cs:945`, `HospitalEndpoints.cs:48`). Hasta decidirlo, nada cambia ahí.

## Cómo se prueba

`backend.Tests` corre con PostgreSQL real: `scripts/test-backend.sh "<filtro>"`. Playwright
(`frontend/tests/*.spec.ts`) corre en local contra el stack, con `page.route` para simular la API.
Hospital: `Hospital.Contract.Tests` y la suite del módulo. **Toda prueba ejercita el código que de
verdad se registra o mapea** (`Hosting`, `MapWhatsApp`, `AgentWorker`); una prueba sobre una copia
pasa en vacío.

## Lote A1 — agente y worker (`AgentRuntime.cs`, `Hosting.cs`)

| # | Criterio | Cómo se prueba | Origen |
|---|---|---|---|
| MR-1 | Con el proveedor de IA caído, el menú (`AgentRuntime.cs:216`) añade «Si es una emergencia, llama al *{EmergencyPhone}*.» cuando el inquilino tiene número; sin número el texto no cambia. | `AgentGuardTests`: con `EmergencyPhone` el texto enviado contiene el número; sin él, texto exacto de hoy. | S1 |
| MR-2 | La ejecución del modelo tiene un presupuesto propio (`AI_TURN_BUDGET_SECONDS`, 90 s por defecto) menor que el plazo del turno, cada intento 30 s con `ClientRetryPolicy(1)`. Al agotarse entra por el `catch` de `:210` (menú/persona, MR-1), no por `RunTenant`, y deja tiempo al respaldo. | `AgentGuardTests`: modelo falso colgado, presupuesto 1 s → `Activity agent_provider`, mensaje al paciente, `Job` en `done`. Con `OPENAI_*` y primario colgado → «Respuesta de respaldo». | S5 |
| MR-3 | Un turno que termina en `failed` envía una sola vez «No pude completar tu solicitud. Ya avisé al equipo del hospital para que te responda por aquí.» (+ número de emergencia si existe) cuando la ventana de WhatsApp está abierta; clave `failed:{jobId}`. | `AgentJobsHealthTests.ClaimedTurnThatFailsLeavesAnErrorTrail` ampliada: Kapso falso recibe un envío; una segunda pasada no envía otro. | S5 |
| MR-4 | Un turno colgado de un inquilino no retrasa a otro: hasta 4 inquilinos en paralelo, cada uno con su scope. | `AgentJobsHealthTests` con `AgentWorker` real: A colgado, B normal → B `done` mientras A sigue `running`. | S5 |
| MR-5 | Tras consumir la propuesta (`:908`), una respuesta de Hospital ilegible (`JsonException`, `KeyNotFoundException`, `InvalidOperationException`, `FormatException`) pasa a una persona con el texto de conciliación y el `Job` no queda `failed`; ningún segundo `POST`. | `AgentAppointmentsTests`: Hospital falso responde `{}` y luego cuerpo malformado. | D6 |

## Lote A2 — integración con Hospital (`HospitalClient*.cs`, `Program.cs`, `AppointmentReminders.cs`)

| # | Criterio | Cómo se prueba | Origen |
|---|---|---|---|
| MR-6 | El token de servicio se reutiliza por `(TokenEndpoint, ClientId)` hasta `exp − 30 s`; `ValidateTokenTenant` sigue en cada petición; nunca se comparte entre inquilinos. | `HospitalPrescriptionTests`: 2 recetas → 1 `POST` al token endpoint; token expirado → 2; token de otro `tenant_id` → `hospital.token_tenant_or_expiry_invalid` sin petición a Hospital. | I3 |
| MR-7 | `GetLatestIssuedPrescriptionIdAsync` verifica el teléfono una vez por llamada. | Misma prueba: 1 `GET v1/patients/{id}`. | I3 |
| MR-8 | Un timeout de Hospital da 504 «Hospital no respondió a tiempo. Comprueba la agenda antes de reintentar.», no 500. | `SecurityTests`: manejador extraído a `Hosting` e invocado con `DefaultHttpContext`; assert de que `Program.cs` lo usa. | I5 |
| MR-9 | Toda `HospitalIntegrationException` deja en el log `Code` y `TraceIdentifier`, sin cuerpo, URL, token ni ids. | Misma prueba con `ILogger` que captura: contiene el código, no el `patientId` centinela. | I6 |
| MR-10 | Toda petición a Hospital lleva `X-Correlation-Id` (Guid): `Job.Id` en el agente, uno por petición en el personal. | `HospitalClinicalTests`/`AgentIntegrationTests`: handler falso valida `Guid.TryParse` y `job.Id`. | I6 |
| MR-12 | `GetPatientAppointmentsAsync` nunca lee `v1/agenda/day`; se borra el fallback. | `AgentGuardTests` con configuración sin `UsePatientAgenda`: Hospital falso rechaza `/v1/agenda/day` y la prueba pasa. | I8 |
| MR-13 | En recordatorios, el envío tiene plazo propio; el timeout de un contacto cuenta como fallo y la sincronización sigue con los demás. | `AppointmentReminderTests` (nuevo) sobre `AppointmentReminderWorker.RunTenant` extraído: contacto 1 cuelga, contacto 2 se sincroniza; un recordatorio vencido se envía aunque la sincronización agote su plazo. | I4 |

## Lote A3 — webhook, envío y guardas (`WhatsAppEndpoints.cs`, `ConversationService.cs`)

| # | Criterio | Cómo se prueba | Origen |
|---|---|---|---|
| MR-14 | Webhook firmado con JSON malformado → 400 sin escrituras; `message` sin `id` se omite y el resto del lote se procesa (200). | `WhatsAppWebhookTests` (nuevo) sobre el `Receive` mapeado: firma inválida 401 sin filas; entrante válido 1 `Message` + 1 `Job`; entrega repetida sin filas nuevas (INV-6). | D10, D12 |
| MR-15 | Un 4xx de Kapso deja el mensaje en `failed`; 5xx o timeout en `uncertain`; ninguno se reintenta. | `AgentIntegrationTests` con Kapso falso 400 y 503: `Message.Status` y un solo envío. | D10 |
| MR-16 | `CrmDb.SaveChangesAsync` rechaza `TenantId ≠ scope` y modificar un `Audit`. | `SecurityTests` (BD real), regresión del comportamiento actual. | D12 |

## Lote B — frontend (`workspace.tsx` y componentes de U5; backend solo `CrmEndpoints.cs`)

| # | Criterio | Cómo se prueba | Origen |
|---|---|---|---|
| MR-17 | Los selectores de paciente de oportunidades, agenda y agente buscan en el servidor (`/contacts?q=`); cancelar/modificar resuelve el contacto por `patientId` en el servidor; las tarjetas muestran el nombre aunque el contacto esté fuera de la primera página. | `crm.spec.ts`: 100 contactos en la página 1 y el buscado solo por `q=`/`patientId=`; agendar y cancelar sin «Vincula este paciente…». | U1 |
| MR-18 | El filtro de estado viaja como `status=`; con filtros y sin resultados: «Sin resultados con estos filtros» + «Quitar filtros»; la búsqueda por nombre avisa que solo mira la página cargada cuando hay más. | `crm.spec.ts`: URL con `status=human`; texto del estado vacío. | U2 |
| MR-19 | La asignación masiva no tiene destino por defecto; quitar responsable es opción explícita con confirmación; un error conserva la selección. | `crm.spec.ts`: botón desactivado hasta elegir; con 409 las casillas siguen marcadas. | U3 |
| MR-20 | Piden confirmación (`ui/alert-dialog.tsx`): activar el agente en una conversación, desconectar Google, pausar un canal, desactivar un acceso, borrar vista/macro/respuesta guardada. Cancelar no envía nada. | `crm.spec.ts`, `team.spec.ts`, `integration-settings.spec.ts`: contador en `page.route`; cancelar 0 peticiones, confirmar 1. | U4, U5 |
| MR-21 | «Atención automática» sale del formulario de la guía; guardar la guía conserva el `agentEnabled` del servidor. | `crm.spec.ts`: `PUT /settings` tras editar solo la guía lleva el `agentEnabled` servido. | U4 |
| MR-22 | La columna «Estado» de canales dice «Activo»/«En pausa». | `integration-settings.spec.ts`. | U5 |
| MR-23 | Fechas y horas de `workspace.tsx:213,700`, `conversation-workspace.tsx:113,614`, `commercial-workspace.tsx:348`, `kapso-inbox.tsx:74,693` usan la zona del hospital y `es-SV`. | Playwright con `timezoneId: 'Asia/Tokyo'` e inquilino en `America/El_Salvador`. | U6, U12 |
| MR-24 | Cambiar de vista añade historial y «Atrás» vuelve; los enlaces internos `/?view=` no recargan. | `crm.spec.ts`: bandeja → agenda, `goBack()` muestra la bandeja; marca en `window` sobrevive al clic. | U7 |
| MR-25 | Con `!canReply`, texto y adjunto del compositor desactivados. | `crm.spec.ts`: `Textarea` `toBeDisabled()`. | U8 |
| MR-26 | Sin autoscroll si el usuario lee más arriba (>80 px del final); con 200 mensajes se ve «Se muestran los últimos 200 mensajes». | `crm.spec.ts`: scroll arriba, nuevo sondeo, `scrollTop` igual. | U9 |
| MR-27 | El día del diálogo de cita no cambia el día de la agenda. | `crm.spec.ts`. | U10 |
| MR-28 | Un error de `/settings` ofrece «Reintentar». | `crm.spec.ts`: 500 y luego 200 tras el clic. | U11 |
| MR-29 | «Dashboard» → «Inicio»; iconos distintos para Equipo y Contactos; Configuración no repite la tarjeta de conexión (enlaza a «Mi hospital»); la navegación lateral marca `aria-current="page"`. | `crm.spec.ts`. | U12, U13 |
| MR-30 | «Agregar seguimiento» en una columna preselecciona esa etapa; columna vacía dice «Sin seguimientos»; la actividad del agente se filtra por `actorRole==='agent_ai'`. | `crm.spec.ts`. | U14 |

## Lote C — Hospital (`Hospital.Scheduling.Endpoints`, `backend/tests/contract`)

| # | Criterio | Cómo se prueba | Origen |
|---|---|---|---|
| MR-31 | El OpenAPI publica `visitKind` y el motivo de cancelación como `enum` con los tokens de `AgendaWireVocabulary`. | `OpenApiBaselineTests` con la línea base regenerada y el diff revisado. | I1 |
| MR-32 | Una prueba lista cada `(método, ruta, propiedad)` que Recepción lee (`HospitalClient*.cs`, `HospitalClinicalClient.cs`, `HospitalCommercialClient.cs`, `AppointmentReminders.cs`) y verifica que existe en el documento emitido, y que los `enum` de MR-31 igualan las listas de `HospitalClient.cs:93,359-364`. | `RecepcionConsumedContractTests` (nuevo) en `Hospital.Contract.Tests`; el rojo se demuestra renombrando una propiedad. | I1 |

## Lote D — documentación y evals

| # | Criterio | Cómo se prueba | Origen |
|---|---|---|---|
| MR-33 | `verification.md:9`, `deployment.md:101`, `hospital-easypanel.md:18` describen el estado actual (conteo medido, conexión por inicio de sesión, cuenta de servicio en < 1 min); las cifras de evals dicen fecha y denominador. | Salida pegada de `scripts/test-backend.sh`; `grep` sin «56 pasaron», «formulario», «siguiente despliegue». | D15, D13 |
| MR-34 | `scripts/eval-agent.sh` imprime «N/M» y sale con error si falla un caso que no está en `evals/agent/known-failures.txt` (empieza con los 8 fallos documentados). | `AgentEvalCasesAreValid`: cada id de `known-failures.txt` existe como caso. | D13 |

## Lote E — decisiones de alto riesgo (después de A–D)

| # | Criterio | Cómo se prueba | Origen |
|---|---|---|---|
| MR-11 | Las llamadas del personal a Hospital (crear, reprogramar, cancelar cita; leer agenda y disponibilidad; y, por decisión del dueño 2026-10-17, la búsqueda de pacientes, el módulo comercial y las sincronizaciones de Google y de recordatorios que lanza una persona) usan el token Keycloak del usuario (como `HospitalClinicalClient`), no la cuenta de servicio; si Hospital responde 403 el personal ve «Tu usuario no tiene permiso para esto en Hospital.». Un 401 o un token de otro hospital es un 403 sin bucle de login, y una cita rechazada así no consume su clave. El agente y los workers siguen con la cuenta de servicio. | Prueba sobre el endpoint mapeado con Hospital falso: `Authorization` es el token del usuario y no el de servicio; 403 → mensaje. En Hospital, la auditoría de la cita registra al usuario (prueba del módulo Scheduling). | S6 |
| MR-35 | **(Retenido hasta aprobación clínica.)** Antes del modelo, estas señales llevan al número de emergencia y a una persona: dolor u opresión en el pecho; no puede respirar / se ahoga; desmayo o pérdida de conciencia; convulsión; sangrado abundante que no para; debilidad o adormecimiento de un lado, cara caída o no puede hablar; ideación suicida o autolesión («me quiero morir», «quitarme la vida»); intoxicación o sobredosis; reacción alérgica grave con hinchazón de cara o garganta. Frases negadas o de terceros sin urgencia no disparan. | `AgentGuardTests`: tabla de frases que disparan y que no; evals `urgencia-*`. | S1 |
| MR-36 | Un envío desde `/inbox` pasa por la API de Recepción (`ConversationService.Send`): pausa al agente (`human`), deja `Activity` y es idempotente por clave del cliente; la web ya no usa `KAPSO_API_KEY` para enviar. `KAPSO_MANUAL_SEND_ENABLED` sigue apagado por defecto. | `server-tests/kapso.test.ts`: el route handler llama a la API y nunca a Kapso; .NET: el endpoint deja `Status=human`, `Activity` y no duplica con la misma clave. | S2 |
| MR-37 | El llavero de DataProtection se protege con un certificado montado fuera de `/keys` y de los respaldos (`DATAPROTECTION_CERT_PATH`/`_PASSWORD`); en Production sin certificado el arranque falla con un mensaje claro; las claves viejas sin proteger siguen legibles. `backup.sh` escribe el dump y el llavero en destinos separados y el llavero cifrado; `docs/deployment.md` describe un ensayo de restauración. | `SecurityTests`: con certificado, el XML de la clave nueva contiene `EncryptedKey`; sin certificado en Production, excepción; payload protegido con una clave vieja se lee. Ensayo de restauración documentado y ejecutado en local. `.env.example` no se toca (un hook lo protege frente a los agentes): las variables viven en `docs/deployment.md` «Llavero protegido». | S4 |
| MR-38 | Cada `recepcion-service-<tenant>` tiene su propia credencial: `client-jwt` con una clave EC P-256 por inquilino que Recepción guarda cifrada y publica en `/hospital-jwks/<tenant>`; ya no usa un secreto compartido. Rotar la de un hospital no afecta a los demás. | Hospital: `RecepcionRealmScriptTests` (sin `secret=`, `client-jwt`, `jwks.url` por inquilino); Recepción: `HospitalConnectionTests` con dos inquilinos (dos `kid` y firmas, JWKS de A sin la clave de B, rotar A no cambia B, ningún `client_secret` con clave, respaldo solo con `invalid_client`). Despliegue en 3 pasos en `hospital-integration.md`. | S3 |
| MR-39 | `Jobs` tiene índice `(TenantId, Status, CreatedAt)`; un barrido diario borra `Jobs` terminados y `Receipts` de más de 30 días. | Migración aplicada en `test-backend.sh`; prueba del barrido con filas de 31 y 29 días. | D8 |
| MR-40 | El estado pendiente del agente (propuesta, formulario de registro, pregunta en curso) vive en una tabla `PendingActions` con expiración, no en `Activity` ni en el texto del mensaje anterior; `Activity` deja de borrarse. | Pruebas existentes de agenda/registro siguen verdes; prueba nueva: cambiar el texto de una pregunta no cambia el flujo; ninguna `Activity` se borra. | D3, D4 |
| MR-41 | Hospital acepta `Idempotency-Key` en `POST v1/agenda` y `POST v1/patients` (por inquilino y llamador): la misma clave devuelve el resultado original sin crear otro; Recepción la envía. | Hospital: prueba del módulo con dos POST iguales → una cita. Recepción: el handler falso recibe la cabecera. | I2 |

## Anti-criterios

- Publicar MR-35 sin aprobación clínica, o ampliar la detección de emergencias por cuenta propia.
- Reintentar un `POST` a Hospital o un envío a Kapso tras timeout o propuesta consumida (INV-6).
- Escribir en logs cuerpos, URLs, tokens, teléfonos o ids de paciente (INV-8).
- Hacer que `/health/ready` dependa de Hospital.
- Compartir el token en caché entre inquilinos o saltarse `ValidateTokenTenant`.
- Probar una copia del manejador, de `Receive` o del bucle del worker; saltar en verde sin `TEST_DATABASE`.
- `<Compile Remove>`, debilitar assertions o ampliar `known-failures.txt` para ir a verde.
- Cambiar `visitKind` mientras OQ-2 siga abierta.
- Guardar el certificado del llavero o cualquier secreto en el repositorio o en el respaldo del dump.

## No se hace

- Partir `workspace.tsx` y `AgentRuntime.cs` o deduplicar (D1, D2, D5): no corrige ningún defecto y chocaría con todos los lotes.
- i18n completo (la app es solo español), OpenTelemetry (MR-9/10 bastan), axe como dependencia.
- «Cargar mensajes anteriores»: necesita cursor en la API; MR-26 avisa del tope.
- Validación global de configuración (D7), compatibilidad de versiones (I14), caché de plantillas (I4).
- `INITIALIZE_DATABASE` por defecto (D9): hay una sola réplica; se revisa si se escala.
- CI de Hospital y pruebas vivas automáticas (I11): bloqueadas por la facturación de GitHub.

## Revalidación de los hallazgos

Falsos o ya resueltos en `origin/main`: D11 (`53842ce`), I7 (los tres roles son personal del
hospital), I9 (`hospital-easypanel.md:42` nombra la excepción), I13 (decisión 2 de
`hospital-onboarding.spec.md`), I15 (cliente nuevo por uso), la `key` del formulario de ajustes
(U11), el chevron y `⌕` (U12), el badge oculto (U13), el asistente de una respuesta (U14), el índice
de `Activity` (D4), el lock sin timeout (D10), el parseo de evals (D13), el `09:00` del grounding
(D15), `/health/ready` y `RemoveAllLoggers` (I6), `Degraded` silencioso (I14).
