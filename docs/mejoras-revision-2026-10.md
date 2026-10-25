# Mejoras de la revisión 2026-10

Origen: revisión del 2026-10-17 (UX, deuda técnica, integración con Hospital), revalidada contra
`origin/main` (Recepción `7b4120c`, Hospital `423c5152`). Invariantes INV-1…INV-8: `docs/backlog.md`.
De 50 hallazgos: 19 vigentes, 20 vigentes en parte, 9 falsos, 2 ya resueltos (tabla al final).

## Decisiones del dueño (2026-10-17)

- **OQ-1 emergencias:** el número de emergencia sale siempre que el modelo falle (MR-1, MR-3). La
  lista ampliada de señales de alarma (MR-35) se propone aquí y **no se publica hasta que el dueño
  o el responsable clínico la apruebe**.
- **OQ-3 `/inbox`:** ~~se conserva la vista; el envío pasa por la API de Recepción (MR-36).~~
  **Retirada por el dueño el 2026-10-25** (sustituye «se conserva la vista»): la única bandeja de
  WhatsApp es `/?view=inbox`, servida por la API de Recepción y multiinquilino. `/inbox` redirige
  ahí; la web ya no lee Kapso ni guarda sus credenciales. Un doctor con número propio entra filtrado
  a ese número (MR-45).
- **OQ-4 llavero:** autorizado proteger el llavero y separar/cifrar su respaldo (MR-37).
- **OQ-5 secretos:** autorizado un secreto por `recepcion-service-<tenant>` (MR-38).
- **OQ-6 migraciones:** autorizadas en Recepción (MR-39, MR-40) y la de idempotencia en Hospital (MR-41).
- **S6 autoría:** las acciones del personal van a Hospital con el token del propio usuario; la cuenta
  de servicio queda solo para el agente (MR-11).
- **OQ-2 decidida por el dueño (2026-10-17), MR-42:** una cita es «primera vez» (`first-visit`) si
  Hospital no tiene **ninguna** cita del paciente anterior a la nueva, de cualquier fecha, sin contar
  las canceladas (`cancelled-by-patient`, `cancelled-by-clinic`); si no, «subsecuente» (`follow-up`).
  Si Hospital no responde, `follow-up`.
- **MR-43, decisión del dueño (2026-10-17), sustituye «cualquiera, sin canceladas» de OQ-2:** «primera
  vez» = el paciente no tiene ninguna cita anterior **a la que llegó**. Si no llegó a su primera cita
  (no-show, sin registrar o cancelada), la siguiente sigue siendo primera vez. Lo decide Hospital en
  `GET v1/agenda/patients/{id}/earlier`; Recepción no cambia su llamada. Las citas ya creadas no se
  recalculan.

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
| MR-23 | Fechas y horas de `workspace.tsx:213,700`, `conversation-workspace.tsx:113,614`, `commercial-workspace.tsx:348` (y `kapso-inbox.tsx`, retirado el 2026-10-25 con `/inbox`) usan la zona del hospital y `es-SV`. | Playwright con `timezoneId: 'Asia/Tokyo'` e inquilino en `America/El_Salvador`. | U6, U12 |
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
| MR-36 | **(Código retirado el 2026-10-25 con `/inbox`, OQ-3; la bandeja `/?view=inbox` ya envía por `ConversationService.Send`.)** Un envío desde `/inbox` pasa por la API de Recepción (`ConversationService.Send`): pausa al agente (`human`), deja `Activity` y es idempotente por clave del cliente; la web ya no usa `KAPSO_API_KEY` para enviar. `KAPSO_MANUAL_SEND_ENABLED` sigue apagado por defecto. | `server-tests/kapso.test.ts`: el route handler llama a la API y nunca a Kapso; .NET: el endpoint deja `Status=human`, `Activity` y no duplica con la misma clave. | S2 |
| MR-37 | El llavero de DataProtection se protege con un certificado montado fuera de `/keys` y de los respaldos (`DATAPROTECTION_CERT_PATH`/`_PASSWORD`); en Production sin certificado el arranque falla con un mensaje claro; las claves viejas sin proteger siguen legibles. `backup.sh` escribe el dump y el llavero en destinos separados y el llavero cifrado; `docs/deployment.md` describe un ensayo de restauración. | `SecurityTests`: con certificado, el XML de la clave nueva contiene `EncryptedKey`; sin certificado en Production, excepción; payload protegido con una clave vieja se lee. Ensayo de restauración documentado y ejecutado en local. `.env.example` no se toca (un hook lo protege frente a los agentes): las variables viven en `docs/deployment.md` «Llavero protegido». | S4 |
| MR-38 | Cada `recepcion-service-<tenant>` tiene su propia credencial: `client-jwt` con una clave EC P-256 por inquilino que Recepción guarda cifrada y publica en `/hospital-jwks/<tenant>`; ya no usa un secreto compartido. Rotar la de un hospital no afecta a los demás. | Hospital: `RecepcionRealmScriptTests` (sin `secret=`, `client-jwt`, `jwks.url` por inquilino); Recepción: `HospitalConnectionTests` con dos inquilinos (dos `kid` y firmas, JWKS de A sin la clave de B, rotar A no cambia B, ningún `client_secret` con clave, respaldo solo con `invalid_client`). Despliegue en 3 pasos en `hospital-integration.md`. | S3 |
| MR-39 | `Jobs` tiene índice `(TenantId, Status, CreatedAt)`; un barrido diario borra `Jobs` terminados y `Receipts` de más de 30 días. | Migración aplicada en `test-backend.sh`; prueba del barrido con filas de 31 y 29 días. | D8 |
| MR-40 | El estado pendiente del agente (propuesta, formulario de registro, pregunta en curso: `emergency`, `person` o `emergency-contact`, que hace de «Emergencia: …» en la respuesta la etiqueta de ese dato y no una urgencia, mientras la pregunta o el formulario que la hizo sigan abiertos) vive en una tabla `PendingActions` con expiración, emparejado con el mensaje que preguntó, no en `Activity` ni en el texto del mensaje anterior; `Activity` deja de borrarse, salvo el `DELETE` único de la migración `PendingActions` (filas de estado `intake`, `visit_kind`, `proposal:%`; `proposal_used:%` se queda, y desde ahora guarda acción, hora y cita, sin datos personales). Transición: un «sí» a una pregunta enviada antes del despliegue (sin `PendingAction`) se empareja por su texto canónico durante 24 h, solo para mensajes del agente anteriores a `AGENT_LEGACY_QUESTIONS_BEFORE` (poner la hora del despliegue; se retira tras 2026-11-17). | Pruebas existentes de agenda/registro siguen verdes; prueba nueva (`AgentPendingActionsTests`): cambiar el texto de una pregunta no cambia el flujo, en cada camino que pregunta; el mismo texto del personal no deriva; ninguna `Activity` se borra; la propuesta se consume antes del POST; pregunta anterior al corte → deriva, posterior sin `PendingAction` → no. | D3, D4 |
| MR-42 | El tipo de visita sigue OQ-2. El agente lo deriva al confirmar, salvo que el paciente dijera `procedure`, `results` o `paperwork`; quien se registra en esa misma confirmación es `first-visit` sin preguntar. El personal elige en el diálogo de cita entre los seis tipos publicados (`first-visit, follow-up, procedure, results, paperwork, same-day-urgent`), preseleccionado con la misma regla (`GET /api/hospital/contacts/{id}/visit-kind?before=`) mientras no toque el selector; sin `visitKind`, el POST aplica la regla; un tipo fuera de la lista se rechaza antes de gastar la clave. La regla es una sola lectura de Hospital, `GET v1/agenda/patients/{id}/earlier?before=<ISO>` → `{ "exists": bool }` (Hospital excluye canceladas y `entered-in-error`); si Hospital no la tiene (404), falla o no responde a tiempo, `follow-up` y la cita se agenda igual, para el agente y para el personal. | Recepción, `VisitKindTests`: `exists=false` → `first-visit`, `true` → `follow-up`, 404/500/timeout → `follow-up` sin derivar; una sola lectura con `before` = inicio de la nueva cita; el personal elige o se aplica la regla, también con la lectura fallando; tipo inválido → 400 sin recibo. Hospital: la regla (cita de hace 3 años → sí; solo canceladas → no) se prueba con su endpoint. | OQ-2 |
| MR-43 | «Primera vez» = sin citas anteriores **a las que el paciente llegó** (decisión del dueño 2026-10-17; sustituye «cualquiera, sin canceladas» de MR-42). Una cita anterior con no-show, sin registrar o cancelada no la convierte en subsecuente. Lo decide Hospital en `GET v1/agenda/patients/{id}/earlier?before=` (misma forma `{ "exists": bool }`); Recepción no cambia su llamada (`FirstOrFollowUpAsync`) ni recalcula citas ya creadas. El texto del diálogo de cita lo dice. | Hospital: la regla con su endpoint (solo un no-show anterior → `exists=false`; una a la que llegó → `true`). Recepción: `VisitKindTests` sigue verde sin cambios (el contrato no cambia). | OQ-2 |
| MR-44 | El perfil del contacto muestra los no-show del paciente vinculado: «No asistió N vez/veces» y, si hay al menos uno seguido, «· M seguida(s)»; con 0 no muestra nada. Lectura en nombre de la persona (MR-11): `GET /api/hospital/contacts/{id}/attendance` → Hospital `GET v1/agenda/patients/{id}/attendance` → `{ "noShows": int, "consecutiveNoShows": int }`; qué cuenta como no-show y como seguido lo define la spec de asistencia de Hospital (`docs/features/17-agenda-y-citas/asistencia-del-paciente.spec.md`). Falla cerrado: sin alguno de los dos campos, con negativos o con `consecutiveNoShows > noShows`, `hospital.invalid_response` (502) y el perfil no muestra la línea. Antes verifica el vínculo como las citas del contacto (`GetVerifiedPatientAsync`): si el teléfono ya no coincide, `hospital.patient_phone_mismatch` sin leer `attendance`. 401/403 de Hospital → `hospital.user_*`. Sin paciente vinculado, 404 sin llamar a Hospital. Si la lectura falla, el perfil se ve igual, sin la línea y sin toast. | `AttendanceTests`: ruta y token del usuario, 404 sin llamada, teléfono que ya no coincide → mismo error que las citas y ninguna lectura de `attendance`, respuesta sin campos, negativa o con más seguidos que total → `hospital.invalid_response`, 403/401 → `hospital.user_forbidden`/`hospital.user_token_rejected`. `server-tests/format.test.ts`: `noShowLine` (0 → nada, singular y plural). Recepción no tiene prueba de contrato de rutas consumidas: la ruta queda en `hospital-integration.md`. | Dueño |
| MR-41 | Hospital acepta `Idempotency-Key` en `POST v1/agenda` y `POST v1/patients` (por inquilino y llamador): la misma clave devuelve el resultado original sin crear otro; Recepción la envía. | Hospital: prueba del módulo con dos POST iguales → una cita. Recepción: el handler falso recibe la cabecera. | I2 |
| MR-45 | Un doctor con número propio (canal con `DoctorId` igual a su `subject` de `/api/me`) entra a la bandeja `/?view=inbox` filtrada a su número; puede quitar el filtro («Todos los canales») y ver las del hospital. Un enlace con `channelId`, `conversationId` o `phone`, o la vista de equipo de un responsable, manda sobre el filtro inicial. Sin cambio de permisos ni de API: `/api/channels` ya publica `doctorId` y `/api/me` el `subject` (`Channel.DoctorId` se valida contra `Member.Subject`, `WhatsAppEndpoints.cs`). | `tests/inbox-doctor.spec.ts` con `/api/crm/me` y `/api/crm/channels` simulados: la lectura de conversaciones lleva `channelId=<canal del doctor>`; tras «Todos los canales» ya no lo lleva; con `?conversationId=` no lo lleva. | Dueño 2026-10-25 |

## Lote F — bandeja de WhatsApp (revisión en navegador del 2026-10-25, `/?view=inbox`)

Asunciones de producto (asumidas, no bloqueantes): **F-1** apagar el agente del hospital no cambia
datos: una conversación con `Status=agent` sigue así en la base y se *muestra* como «Sin atender»;
**F-2** la ventana cerrada no impide activar el agente (responde al siguiente mensaje del paciente, que
la reabre); sólo lo impiden el número desactivado o el envío automático apagado; **F-3** la búsqueda
de la bandeja no busca en el cuerpo de los mensajes: B16 sólo filtra dentro de una conversación
(`chat-panel.tsx`), no hay búsqueda de mensajes en servidor que reutilizar.

| # | Criterio | Cómo se prueba | Origen |
|---|---|---|---|
| MR-46 | Con el agente del hospital apagado (`Tenant.AgentEnabled=false`), una conversación abierta con `Status=agent` se muestra «Sin atender» (estilo de aviso) en la tarjeta, la cabecera del chat y la cabecera móvil, nunca «Agente IA»; el filtro «Agente» no la cuenta y «Personas» sí; `/overview` la suma a `human` («Esperando a tu equipo», insignia de la barra lateral) y `agent` es 0. Asumida F-1. | .NET `InboxListTests`: agente apagado → `status=agent` vacío, `status=human` la incluye, `Overview.Human` la cuenta y `Agent=0`; encendido → como antes. Playwright `inbox-ux.spec.ts`: con `/me` `agentEnabled=false` la tarjeta y la cabecera dicen «Sin atender» y no «Agente IA». | Revisión F1 |
| MR-47 | Un traspaso urgente del agente (`Handoff(urgent: true)`) sube la prioridad a «urgent». Mientras el último traspaso de la conversación es del agente y ninguna persona ha respondido después, `GET /conversations` publica `handoff: { reason, at }`; la tarjeta muestra «Transferida» y el motivo en lugar del último mensaje, y la cabecera del chat el motivo. Lo que recibe el paciente no cambia. | .NET: el traspaso urgente deja `Priority=urgent` y el no urgente no la toca; `handoff` presente tras el traspaso y `null` tras una respuesta humana o un traspaso de una persona; los textos enviados al paciente siguen iguales (pruebas existentes de `AgentPersonChoiceTests`/`AgentGuardTests`). Playwright: tarjeta con «Transferida» y el motivo; cabecera con el motivo. | Revisión F2 |
| MR-48 | `GET /conversations` publica por conversación `sendBlock` (`null` o `{ code, reason }`, `code` ∈ `channel`, `window`, `paused`) con la **misma** regla que `RequireSendable` (una sola función, `ConversationService.SendBlock`). La conversación abierta muestra el motivo como aviso fijo sobre el redactor y «Enviar» y «Adjuntar» quedan deshabilitados y descritos por ese aviso; con la ventana cerrada dice que WhatsApp sólo permite plantillas aprobadas hasta que el paciente vuelva a escribir. Ningún texto de error del envío nombra al proveedor («Kapso»). | .NET `InboxListTests`: canal apagado, ventana cerrada y envío en pausa dan su código y el mismo texto que lanza `RequireSendable`; `InboxSendTests.Refused_send_changes_nothing` además comprueba que el texto no contiene «Kapso». Playwright: con `sendBlock` el aviso es visible antes de escribir y «Enviar» está deshabilitado. | Revisión F3 |
| MR-49 | «Activar agente» avisa en su confirmación que la conversación quedará sin responsable y que el agente responderá los próximos mensajes; se deshabilita, con el motivo visible, cuando el agente no podría enviar (`agentBlock`: número desactivado o envío automático apagado; asumida F-2). Cuando una persona toma la conversación, la actividad dice «{persona} tomó la conversación.», no «Atención humana solicitada». | Playwright: el texto de la confirmación; con `agentBlock` el botón está deshabilitado y el motivo visible. .NET: el PATCH `status=human` deja la actividad «{Nombre} tomó la conversación.». | Revisión F4 |
| MR-50 | Cambiar el estado de la conversación abierta a uno fuera del filtro activo no cierra el chat: sigue abierta hasta que la persona elige otra o vuelve. | Playwright: filtro «Abierta», la conversación pasa a «Pendiente» (la lista ya no la trae) y el chat sigue visible. | Revisión F5 |
| MR-51 | Posponer es un solo control («Posponer 1 h / 1 día / 1 semana») que fija estado y hora juntos; elegir otro plazo con la conversación pospuesta la reprograma. El selector de estado no ofrece «Pospuesta» sin plazo. | Playwright: cada botón envía `state=snoozed` con `snoozedUntil` ≈ ahora + plazo; con la conversación ya pospuesta, otro botón vuelve a enviar con el nuevo plazo. | Revisión F6 |
| MR-52 | Las tarjetas muestran las etiquetas de la conversación (hasta 2 y «+N»). El filtro por etiqueta es un selector con las etiquetas existentes del hospital, leídas de `GET /conversations/labels`. | .NET: `labels` devuelve las etiquetas distintas del inquilino, ordenadas, sin las de otro. Playwright: la tarjeta con 3 etiquetas muestra 2 y «+1»; el selector lista las de `/conversations/labels` y elegir una envía `label=`. | Revisión F7 |
| MR-53 | La búsqueda de la bandeja va al servidor (`q=`): nombre y teléfono del contacto (cifrados: mismo recorrido que `CrmEndpoints.Contacts`, una sola regla `ContactMatches`) y etiquetas de la conversación, dentro del inquilino. Asumida F-3. | .NET: `q` encuentra por nombre sin acentos, por dígitos del teléfono y por etiqueta; nunca una conversación de otro inquilino. Playwright: escribir en «Buscar» envía `q=`; ya no aparece «solo revisa las 100 conversaciones». | Revisión F8 |
| MR-54 | Abrir una conversación escribe `conversationId` en la URL (`history.replaceState`); recargar la vuelve a abrir. Cerrarla lo quita. | Playwright: tras abrir, la URL lleva `conversationId=<id>`; tras recargar el chat sigue abierto. | Revisión F9 |
| MR-55 | A 1280–1440 px la columna de la lista mide 320 px (384 px desde 1536) y el selector «quién atiende» ocupa su propia fila, sin que el contador de «Más filtros» tape «Agente». | Capturas a 1280/1440/1920; Playwright: las cajas de «Agente» y «Más filtros» no se solapan a 1440. | Revisión F10 |
| MR-56 | A 360 px la cabecera del chat muestra quién atiende (incluido «Sin atender»), y con un chat abierto se ocultan el título de página y la búsqueda. | Playwright (proyecto móvil): con chat abierto el estado es visible y el `h1` «Bandeja de entrada» no. | Revisión F11 |
| MR-57 | El selector de vistas guardadas muestra una vista sólo mientras los filtros coinciden con ella (si no, vuelve a «Mis vistas guardadas»), y aplicar una vista con filtros ocultos abre «Más filtros». | Revisión visual; lógica en `server-tests/inbox-filters.test.ts` (`sameFilters`). | Revisión F12 |
| MR-58 | Desde 1280 px, con la tercera columna visible, no se muestra «Ver información del paciente». | Playwright (escritorio 1440): el botón no está. | Revisión F13 |
| MR-59 | Insertar una respuesta guardada la pone en el cursor del borrador, sin borrar lo escrito. | Revisión manual; sin lógica nueva más allá de `setRangeText`. | Revisión F14 |
| MR-60 | En el panel de contexto el expediente («Vincular paciente» / «Ver paciente vinculado») va primero. | Revisión visual. | Revisión F16 |
| MR-61 | Cada número dice qué cuenta: la insignia de la barra lateral se anuncia «N esperando a tu equipo»; Equipo dice «sin resolver y sin responsable (incluye las del agente)». | Revisión visual y nombre accesible de la insignia. | Revisión F17 |

## Estado (2026-10-17)

Commits de Recepción salvo «Hospital». Fecha de todos: 2026-10-17.

| # | Estado | Commits |
|---|---|---|
| MR-1…MR-5 | Hecho | `692b795`, `bd4822a` |
| MR-6…MR-10, MR-12, MR-13 | Hecho | `ab74203`, `5d2c326` |
| MR-11 | Hecho | `4ea29a0`, `6cbc457`, `b7d0d43`; Hospital `a0e50575` |
| MR-14, MR-15, MR-16 | Hecho | `41e382e`, `04d9b68` |
| MR-17…MR-30 | Hecho | `5c055ba`, `71ccbb5`, `b638cff`, `52afc55`, `8e0e088` |
| MR-31, MR-32 | Hecho | Hospital `bc35077e`, `342d1dd8` |
| MR-33 | Hecho | `verification.md`, `deployment.md`, `hospital-easypanel.md` |
| MR-34 | Hecho | `eval-agent.sh`, `evals/agent/known-failures.txt` (arranca con los 2 casos inestables medidos el 2026-10-17; los «8 fallos documentados» del criterio no se identificaron como evals que fallen hoy: el único «8 fallos» de `reception-agent.md` son rojos previos a un arreglo del 2026-10-03), `KnownFailuresAreCasesWithAReason` |
| MR-35 | Hecho, con la lista aprobada por el dueño | `2c7d640`, `ccf232f`, `8c25ed0`, `1d55113`, `9cd7ee9` |
| MR-36 | Hecho | `222524e`, `8754830` |
| MR-37 | Hecho | `e8479d4`, `5ea7282` |
| MR-38 | Hecho: `client-jwt` por hospital con JWKS servida por Recepción; rotar revoca la clave vieja de inmediato (sin gracia, decisión del dueño 2026-10-17); despliegue en 3 pasos | Recepción `HospitalConnectionTests`; Hospital `RecepcionRealmScriptTests`, `ServiceAccountReconcilerTests` |
| MR-39 | Hecho | `2deb945`, `834079f` |
| MR-40 | Hecho; nits de revisión en `mr/rec-d` | `4aabc9d`, `5a3c5d0`, `1e16e28` |
| MR-41 | Hecho. Recepción envía `Idempotency-Key` en `POST v1/agenda` y `POST v1/patients`. Lo que protege: la **creación de cita del personal** que vence por tiempo libera su recibo, y repetirla con la misma clave (el navegador la reusa para el mismo cuerpo) devuelve la cita original en Hospital, sin duplicado; cancelar y reprogramar no llevan clave y siguen gastando el recibo. En el agente (`agent:`/`agent-register:` + id de la propuesta) es defensa en profundidad: la propuesta se consume antes de escribir, así que no hay reenvío, y un resultado incierto sigue yendo a conciliación. Un 409 `*.idempotency_key_reused` se concilia, nunca se reintenta | Hospital `4daedd9d`, `71187473`, `c1655713`; Recepción rama `mr/rec-41`, `HospitalIdempotencyTests` |
| MR-42 | Hecho; la regla la refina MR-43 | `4aabc9d`, `91a4f25`, `5a3c5d0`; Hospital `c971eab7`, `fe0fd635` |
| MR-43 | Hecho en Recepción; depende de Hospital (`mr/hosp-43`) — desplegar Hospital antes | Recepción rama `mr/rec-43` |
| MR-44 | Hecho en Recepción; depende de Hospital (`mr/hosp-43`) — desplegar Hospital antes (sin `attendance` la lectura falla y el perfil no muestra la línea) | Recepción rama `mr/rec-43`, `AttendanceTests` |
| MR-45 | Hecho (2026-10-25), sin cambio de backend | `tests/inbox-doctor.spec.ts` |
| MR-46…MR-61 | Hecho (2026-10-25). Sustituyen: MR-53 retira el aviso «solo revisa las 100 conversaciones» de MR-18 (la prueba de MR-18 ahora comprueba `q=`); MR-54 hace que recargar reabra el chat (`clinical.spec.ts` MR-44 y `session.spec.ts` lo esperan); MR-48 deshabilita «Enviar» en las conversaciones demo con la ventana cerrada (`session.spec.ts` las sirve como enviables: su tema es la sesión). Sin búsqueda en el cuerpo de los mensajes (F-3) | .NET `InboxListTests` (+13), `AgentPersonChoiceTests`; `tests/inbox-ux.spec.ts` (+24); `server-tests/inbox-filters.test.ts` (`sameFilters`) |

## Anti-criterios

- Publicar MR-35 sin aprobación clínica, o ampliar la detección de emergencias por cuenta propia.
- Reintentar un `POST` a Hospital o un envío a Kapso tras timeout o propuesta consumida (INV-6).
- Escribir en logs cuerpos, URLs, tokens, teléfonos o ids de paciente (INV-8).
- Hacer que `/health/ready` dependa de Hospital.
- Compartir el token en caché entre inquilinos o saltarse `ValidateTokenTenant`.
- Probar una copia del manejador, de `Receive` o del bucle del worker; saltar en verde sin `TEST_DATABASE`.
- `<Compile Remove>`, debilitar assertions o ampliar `known-failures.txt` para ir a verde.
- Contar como «cita previa» una a la que el paciente no llegó (cancelada, no-show, sin registrar), o limitar la búsqueda a una ventana fija (OQ-2, MR-43).
- Recalcular el tipo de visita de citas ya creadas (MR-43); contar «sin registrar» como no-show o mostrar un error si la lectura de asistencia falla (MR-44).
- Guardar el certificado del llavero o cualquier secreto en el repositorio o en el respaldo del dump.
- Mostrar «Agente IA» o contar en «Con el agente» una conversación que nadie atiende porque el agente del hospital está apagado, o cambiar su `Status` en masa para ocultarlo (MR-46).
- Calcular en el navegador una regla de envío distinta de `RequireSendable`, o dejar «Enviar» activo cuando el servidor ya sabe que rechazará el envío (MR-48).
- Cambiar lo que el paciente recibe en un traspaso (MR-47), o prometer «El agente responderá» cuando no puede enviar (MR-49).
- Buscar fuera del inquilino o truncar la búsqueda a la página cargada (MR-53).

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
