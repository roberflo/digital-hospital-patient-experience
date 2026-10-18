# Integración con Hospital

Contratos comprobados en el código de `../Hospital/backend/src` el 2026-10-02. El adaptador compilable vive en `backend/Integrations/HospitalClient.cs`. El bridge de recetas se aplicó al hospital autorizado; ver integrations/hospital/README.md para archivos y pruebas.

## Identidad y aislamiento

- El hospital exige JWT Keycloak firmado, audiencia/issuer válidos, `sub` GUID y `tenant_id` GUID no vacío. No acepta tenant en body, query ni un header alternativo.
- Única excepción, y sólo en la API de Recepción: el dueño de plataforma (rol `platform-owner`, token sin `tenant_id`) indica la recepción en cuyo nombre configura con la cabecera `X-Acting-Tenant`, que pone el proxy del servidor web desde una cookie sellada, nunca el navegador ([platform-owner.md](platform-owner.md)). Esa cabecera en un token que no es de plataforma se rechaza, no se ignora; un token con `platform-owner` y `tenant_id` también. El token del dueño no se reenvía a Hospital: las llamadas en nombre siguen usando la cuenta `recepcion-service-<tenant>`, y ninguna ruta clínica ni de pacientes es operable por plataforma.
- Lee los roles desde `realm_access.roles`, con etiquetas exactas: `Recepción`, `Admisión`, `Enfermería`, `Médicos`, `Odontólogos`, `Nutricionistas`, `Administrador`.
- Recepción no tiene roles propios: deriva sus tres perfiles de esos siete (tabla en [deployment.md](deployment.md)) y deniega cualquier otro. Administrador no tiene acceso clínico en Hospital.
- Cada tenant CRM usa una configuración de hospital independiente. Su identificador debe coincidir con el `tenant_id` del token de servicio. El adaptador rechaza token ajeno, expirado o sin claim antes de hacer llamadas. El hospital sigue validando firma/issuer/audience; decodificar localmente el payload sólo evita errores de enrutamiento de credenciales configuradas.
- Registrar el HttpClient con timeout de 30 segundos, máximo de respuesta razonable y redirecciones deshabilitadas. No aplicar retries automáticos a escrituras.

```csharp
services.AddHttpClient<HospitalClient>(client => {
    client.Timeout = TimeSpan.FromSeconds(30);
    client.MaxResponseContentBufferSize = 20 * 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
```

Configurar secretos por entorno (UUID ilustrativo, sin credenciales reales):

La conexión de cada hospital se deriva de los ajustes de instalación (`HOSPITAL_API_URL`) y de su cuenta `recepcion-service-<UUID>` en el realm: no se configura por hospital.

### MR-38 — una credencial por hospital

Cada `recepcion-service-<UUID>` se autentica con `client-jwt` (RFC 7523): Recepción genera, en la primera petición de token de ese hospital, una clave EC P-256 que guarda dentro de su `HospitalConnection` (cifrada, sin migración) y firma un `client_assertion` ES256 (`iss = sub = client_id`, `aud = Auth__Authority`, `jti`, 60 s). Keycloak lee la clave pública en `GET /hospital-jwks/<UUID>` de la API de Recepción: anónimo, fuera de `/api`, solo claves públicas y `{"keys":[]}` para un hospital sin clave o desconocido. El token sigue pasando por `ValidateTokenTenant` y por la caché de MR-6, ahora con el `kid` en la clave de caché. Un Administrador rota la clave de su hospital con `POST /api/hospital/connection/key` (auditado `hospital.key_rotated`); los demás hospitales no cambian. Tras rotar, Keycloak puede tardar ~10 s en volver a leer la JWKS: una petición de token en ese intervalo puede fallar una vez.

Probado contra Keycloak 26.4 el 2026-10-17: `aud` debe ser el emisor **público** (`Auth__Authority`), aunque la petición llegue por `KEYCLOAK_INTERNAL_ISSUER`; con el emisor interno responde `invalid_client` «Invalid token audience».

Despliegue, en este orden:

1. **Recepción** con esta versión, conservando `HOSPITAL_SERVICE_CLIENT_SECRET`. Mientras las cuentas sigan siendo de secreto, Keycloak responde `invalid_client` a la firma y Recepción reintenta una vez con el secreto (rama `ponytail:` en `HospitalClient.GetTokenAsync`). Cualquier otro fallo no envía el secreto.
2. **Hospital**: `KC_RECEPCION_JWKS_URL_BASE=http://<api-recepción>:8080/hospital-jwks` en `keycloak-config` y `keycloak-service-accounts`, quitar `KC_RECEPCION_SERVICE_CLIENT_SECRET`, desplegar. El job convierte todas las cuentas; comprobar la conexión desde Recepción.
3. **Recepción**: quitar `HOSPITAL_SERVICE_CLIENT_SECRET` y redesplegar; después borrar la rama de respaldo y la línea `ClientSecret` de `HospitalConnectionStore.Defaults`.

Una conexión guardada a mano con `PUT /api/hospital/connection` (`ClientId`/`ClientSecret` explícitos) usa el mismo orden: primero la clave, y su secreto solo si Keycloak responde `invalid_client`. Las claves `Hospital__Tenants__<UUID>__...` siguen existiendo sólo para excepciones y para autorizar la entrega de recetas:

```text
Hospital__Tenants__11111111-1111-1111-1111-111111111111__BaseUrl=https://hospital.example/
Hospital__Tenants__11111111-1111-1111-1111-111111111111__TokenEndpoint=https://identity.example/realms/hospital/protocol/openid-connect/token
Hospital__Tenants__11111111-1111-1111-1111-111111111111__ClientId=recepcion-hospital-1
Hospital__Tenants__11111111-1111-1111-1111-111111111111__ClientSecret=<secret-store>
Hospital__Tenants__11111111-1111-1111-1111-111111111111__AllowClinicalDelivery=false
```

Para pruebas se admite `AccessToken` en lugar de client credentials, pero expirará y deberá renovarse. El token de servicio necesita el mapper `tenant_id` del mismo hospital y únicamente los permisos de su función. Nunca compartir un token de plataforma entre hospitales. HTTPS es obligatorio fuera de una red privada de contenedores confiable; el adaptador admite HTTP para esa red interna.

## Contratos existentes

| Operación | HTTP real | Condiciones |
|---|---|---|
| Paciente vinculado | `GET /v1/patients/{patientId}` | Responde `patientId`, `givenNames`, `familyNames`, `phone`, etc. El adaptador devuelve una proyección mínima. |
| Buscar paciente | `GET /v1/patients/search?queryShape=...&term=...` | Sólo `dui`, `record-number`, `name-tokens`; **no existe búsqueda por teléfono**. |
| Disponibilidad | `GET /v1/agenda/booking-options?from=YYYY-MM-DD&to=YYYY-MM-DD&clinicianId=UUID` | `professionals[].days[].slots[]`: `offered`, `takenBy`, `startsAt`, `durationMinutes`. Respetar `maxDaysPerQuery` y estados de día. |
| Citas de paciente | `GET /v1/agenda/patients/{patientId}?from=YYYY-MM-DD&to=YYYY-MM-DD` | Nueva ruta aplicada en Hospital, máximo 31 días; ver `integrations/hospital/PATIENT-AGENDA.md`. |
| Agenda | `GET /v1/agenda/day?clinicalDay=YYYY-MM-DD` | Vista de personal; no pasar filas de otros pacientes al LLM de una conversación. |
| Comprobar cita | `GET /v1/agenda/{id}?patientId=UUID` | Hospital valida pertenencia exacta. El adaptador repite comparación antes de mutar. |
| Agendar | `POST /v1/agenda` | `{patientId, clinicianId, startsAt, durationMinutes, visitKind}` → `{appointmentId,status,overlaps}`. |
| Reprogramar | `POST /v1/agenda/{id}/reschedule` | `{startsAt,clinicianId,durationMinutes}` → 204. |
| Cancelar por paciente | `POST /v1/agenda/{id}/cancel` | `{reason:"patient-requested",cancelledByPatient:true}` → 204. Nunca DELETE. |
| Listar recetas por bot | `POST /v1/reception/patients/{id}/prescriptions/list` | Body phone/cursor; sólo IDs signed. Requiere cuenta servicio dedicada y teléfono actual. |
| Consultar receta ya emitida | `POST /v1/reception/prescriptions/{id}` | Body phone; DTO mínimo, paciente coincidente, signed. |
| PDF receta por bot | `POST /v1/reception/prescriptions/{id}/pdf` | Body phone → application/pdf. Reutiliza mapper/renderer y perfil del firmante. |

`visitKind`: `first-visit`, `follow-up`, `procedure`, `results`, `paperwork`, `same-day-urgent`. Las fechas de citas llevan offset; los días de agenda pertenecen a la zona horaria del hospital.

## Verificación del paciente

La primera vinculación se hace por personal autorizado: el contacto CRM guarda el `patientId` y el teléfono WhatsApp de la conversación. Antes de cada acción privada se consulta el paciente hospitalario y se exige igualdad del teléfono normalizado completo. No se adivina código de país ni se compara un sufijo. Un teléfono ausente/cambiado o paciente no vinculado causa transferencia a humano. Familiares, tutores y teléfonos compartidos requieren vinculación explícita y autorización distinta; el adaptador no concede ese acceso por coincidencia con un contacto de emergencia.

## Límites reales y decisiones

1. **Carrera de agenda:** el hospital admite solapamientos deliberadamente y devuelve `overlaps`. Consultar slots ofrecidos antes de reservar evita la mayoría de conflictos pero no da exclusión atómica. La UI debe mostrar y derivar un `overlaps=true`; un timeout después del POST es resultado incierto que exige reconciliar, nunca repetir ciegamente. Un endpoint de reserva con idempotencia y bloqueo del slot sería la ampliación mínima para garantizar reservas automáticas sin doble cita.
2. **Recetas por agente:** las rutas clínicas actuales requieren un rol clínico. `AllowClinicalDelivery` permanece desactivado para el bot hasta desplegar el puente estrecho descrito en `integrations/hospital`. Se requieren ambos flags AllowClinicalDelivery y UseReceptionBridge; no existe fallback hacia rutas clínicas generales. Activar los flags no otorga permiso al hospital. Dar al bot el rol `Médicos` para desbloquearlo sería un defecto.
3. **Contenido clínico:** el agente no firma, cancela, repite ni modifica recetas; interpreta únicamente instrucciones textuales ya emitidas. Dudas sobre dosis nuevas, tratamiento o síntomas se transfieren al doctor.
4. **Adjuntos:** el PDF se obtiene como bytes en memoria. No se publica una URL permanente que revele recetas, ni se devuelve la URL de almacenamiento interno al usuario.
5. **Google Calendar:** Hospital no publica integración Google Calendar. El CRM puede sincronizar agenda en una dirección con sondeo periódico y clave de evento por tenant/cita, conservando Hospital como fuente. Cambios externos no deben sobrescribir su agenda sin un contrato de conciliación. El espejo de Google debe llevar título genérico y datos mínimos, nunca receta/diagnóstico.

## Verificación ejecutada

`tests/hospital-client/HospitalClient.Checks.csproj` es un ejecutable sin paquetes externos. Usa fixtures sintéticos y un HttpMessageHandler controlado; prueba teléfono, tenant, expiración, IDOR de citas/recetas, rechazo clínico antes de red, errores sin contenido sensible, ruta/body de creación y propagación de solapamientos.

Compilado y ejecutado con SDK 8 en modo de compatibilidad porque el host sólo ofrece ese SDK:

```text
dotnet msbuild tests/hospital-client/HospitalClient.Checks.csproj /p:TargetFramework=net8.0 /p:RestoreConfigFile=/tmp/recepcion-hospital-checks.NuGet.Config /t:Restore,Build
dotnet tests/hospital-client/bin/Debug/net8.0/HospitalClient.Checks.dll
PASS: hospital client phone linkage, tenant/expiry guard, cross-patient appointment and prescription isolation, clinical gate, error redaction, exact booking contract and overlap signal.
```

Con SDK 10: `dotnet run --project tests/hospital-client/HospitalClient.Checks.csproj`. Esto prueba el adaptador, no sustituye pruebas reales de Keycloak, base de datos ni el bridge en Hospital.

Ese mismo comando se ejecutó posteriormente en `mcr.microsoft.com/dotnet/sdk:10.0` y también pasó. El puente ya está en Hospital `main` y se validó con pruebas dirigidas: 33 del servicio/HTTP-JWT, 12 de arquitectura, 3 de baseline OpenAPI y 1 del cliente TypeScript generado, sin fallos ni omitidas. Ver `integrations/hospital/README.md` para evidencia y verificaciones de despliegue pendientes.

## Ampliación de agenda (2026-10-02)

La bandeja consulta próximas citas por paciente vinculado mediante el nuevo endpoint acotado. El agente usa solo ese endpoint: nunca lee la agenda del día completa (MR-12). La UI de Agenda comunica errores de disponibilidad, conserva la clave del intento y registra acciones exitosas en el historial CRM. La validación usa la zona horaria del hospital, incluido un horario UTC que corresponde al día clínico anterior.

Conexión local autorizada y activa desde el 2026-10-03. Crear, consultar, reprogramar y cancelar pasó desde API e interfaz de Recepción, contrastando cada estado con Hospital. Ver `integrations/hospital/PATIENT-AGENDA.md` para evidencia y procedimiento.

## Consulta clínica por el doctor desde la bandeja (2026-10-03)

**Expediente y recetas** aparece para el doctor dentro de la conversación. Requiere un paciente vinculado y una conversación asignada al propio doctor, con el agente pausado. Permite leer el historial paginado, recetas, notas firmadas, antecedentes y alergias. Conserva estados como no preguntado, ausencia declarada, contenido restringido, cancelado/anulado y expediente migrado. Es lectura; no firma ni modifica documentos y no transmite datos clínicos al agente ni al chat.

Las nuevas rutas `GET /api/hospital/conversations/{id}/clinical/{timeline|antecedentes|allergies}` y `.../{prescriptions|notes}/{documentId}` usan **el JWT del doctor autenticado**. Nunca utilizan la cuenta de servicio de recepción. Hospital valida firma, audiencia, rol, tenant y permisos clínicos; registra el acceso con la identidad real. Recepción verifica tenant, asignación, teléfono y pertenencia del documento, y agrega auditoría con identificadores, sin contenido clínico. Cada consulta vuelve a comprobar el vínculo. La ventana descarta sus datos al cerrarse o expirar la sesión. No hay caché clínica compartida ni almacenamiento del navegador. Las antiguas rutas clínicas de contactos devuelven 410 para doctores: migrar consumidores a las rutas acotadas por conversación. El puente privado de entrega de recetas al paciente no cambia.

**Qué identidad usa cada llamada a Hospital (MR-11, decisión del dueño 2026-10-17).** Lo que hace una persona viaja con **su JWT**, para que Hospital aplique sus permisos y la audite con su identidad: agenda del día, disponibilidad y citas de un contacto (`GET /api/hospital/agenda`, `/availability`, `/contacts/{id}/appointments`); crear, reprogramar y cancelar cita (`POST /api/hospital/appointments`); búsqueda de pacientes (`POST /api/hospital/contacts/{id}/patients/search`) y vínculo del paciente (`POST /api/contacts/{id}/patient`); el módulo comercial (`/api/commercial/companies`, `/services`, `/contacts/{id}`, `/contacts/{id}/link`, `/contacts/{id}/sync`, `/opportunities/{id}/quote`, `/opportunities/{id}/purchase`, y el antiguo `GET /api/companies`); y las sincronizaciones que lanza un Administrador (`POST /api/google/sync`, `POST /api/appointment-reminders/sync`). Siguen con **la cuenta de servicio**: el agente de WhatsApp, los workers en segundo plano (sincronización comercial, recordatorios, calendario de Google), los ajustes y la conexión (`/api/settings`, `/api/hospital/connection/check`), la sincronización de identidad al abrir un hospital y el propietario de la plataforma. Con el JWT del usuario, Recepción rechaza sin llamar a Hospital un token de otro hospital o a punto de caducar; un 401 de Hospital llega como 403 `hospital.user_token_rejected` (sin bucle de login) y un 403 como `hospital.user_forbidden`: «Tu usuario no tiene permiso para esto en Hospital.». Una cita que Hospital rechaza así no consume su `Idempotency-Key`, y una compra rechazada así no queda «en comprobación» bloqueando el contacto. Las sincronizaciones manuales cuidan la vida del token (300 s): la de recordatorios se rechaza al inicio si le quedan menos de 2 minutos («Tu sesión está por vencer; recarga y vuelve a intentar.»), dura como mucho 90 s (pasado ese plazo responde 504 y la completa el worker) y, si Hospital rechaza a la persona, responde ese 403 en vez de «Agenda verificada»; la de Google lee primero todos los días de Hospital y sólo después escribe en Google, y su auditoría local nombra al Administrador. El BFF renueva el token 60 s antes de caducar.

**Continuar con mi cuenta del hospital** es el único ingreso, también en desarrollo. El cliente `recepcion-web` y las cuentas de servicio `recepcion-service-<tenant>` los declara el job `keycloak-config` de Hospital (ver [deployment.md](deployment.md)); aquí sólo se configuran issuer, client ID, secret y audiencia `hospital-api`. **Cerrar sesión** termina también la sesión de Keycloak. El issuer y las credenciales deben corresponder al mismo realm que valida Hospital.

Para el Hospital sintético local: añadir los `KC_RECEPCION_*` a `Hospital/.env` con `KC_RECEPCION_SERVICE_TENANTS=cccccccc-cccc-4ccc-8ccc-cccccccccccc`, volver a ejecutar su job `keycloak-config` y luego `python3 scripts/connect-hospital-local.py`, que copia esos secretos al `.env` ignorado tras comprobar que el token de servicio lleva exactamente `Recepción` + `reception-agent`. No crea nada en Keycloak ni usa su contraseña de administrador. Los usuarios son los sintéticos `dev-<rol>-<a|b|c>` del realm. `KEYCLOAK_INTERNAL_ISSUER` permite alcanzar Keycloak dentro de Docker conservando el issuer público. Aplicar `docker-compose.hospital.yml` conecta también el frontend a esa red y evita la colisión del alias `api` de ambos proyectos. No se toca el código ni se amplían permisos del Hospital.

Verificación opt-in: `node tests/hospital_clinical_live.cjs`. Usa la cuenta médica sintética C existente, una conversación temporal en un canal deshabilitado y el paciente sintético ya vinculado por `tests/hospital_live.py`. Comprueba denegación sin asignación, login real, lectura de historial/antecedentes/alergias y documentos mediante el BFF, y abre el lector en navegador. Elimina sus fixtures al terminar; conserva la auditoría de acceso. Nunca envía WhatsApp ni escribe expedientes.

Evidencia local: inicio OAuth real de `dev-medicos-c`; historial, antecedentes y alergias HTTP 200; una receta firmada y una nota firmada leídas desde Hospital a través del BFF de Recepción. Lector verificado visualmente con esa receta. Pruebas: 92 backend, 98 API, 54 navegador (escritorio/móvil), 12 servidor frontend; compilación, tipos, lint y formato aprobados.
