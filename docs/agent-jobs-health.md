# Trabajo interrumpido con rastro y salud de las colas

Petición (2026-10-04): aplicar a los workers el modelo de `trycompai/crm` (cola con lease,
reconciliación, salud, motivo visible). Medido contra el árbol, este documento cubre solo lo que
tiene un defecto demostrable: el rastro del abandono y la medida de salud. La cola con lease y el
seguimiento agendado por el agente quedan fuera, por nombre, en «Fuera de alcance».

## Lo que ya existe

- `Job` y `AppointmentReminder` ya son colas de filas (`Models.cs`); el reclamo es atómico:
  `UPDATE` condicional en `AgentWorker` (`AgentRuntime.cs:634`) y candado asesor por inquilino más
  relectura en `AppointmentReminderService.Dispatch` (`AppointmentReminders.cs:85-87`).
- El abandono ya se retira: `Job` en `running` más de 5 min pasa a `uncertain` y la conversación a
  `human` (`AgentRuntime.cs:630-631`); un recordatorio en `sending` más de 10 min pasa a `uncertain`
  con `Reason` (`AppointmentReminders.cs:44-49`).
- Nunca se reintenta lo incierto: `docs/backlog.md` INV-6 y `docs/appointment-agent.md` criterio 6.
- Los recordatorios ya muestran motivo y última sincronización al administrador
  (`ReminderEndpoints.cs:13-22`, `frontend/src/components/appointment-reminders.tsx`).
- Falta: el abandono de un `Job` no deja `Activity` ni `Summary`; no hay conteos de salud; ninguna
  prueba ejercita `AgentWorker`.

## Lenguaje

Un **turno** es un `Job` del agente. Se **abandona** cuando lleva más de 5 min en `running` (el
plazo del turno es 2 min, `AgentRuntime.cs:636`). **Retirar** es pasarlo a `uncertain` y dejar la
conversación con una persona. La **salud** es un conteo de lo que debería haberse movido y no se movió.

## Invariantes

- INV-1: un `Job` retirado por abandono nunca vuelve a `pending` ni a `running`.
- INV-2: toda conversación que el barrido pasa a `human` recibe, en la misma transacción, una
  `Activity` con el motivo y ese motivo en `Summary`.
- INV-3: el barrido es idempotente; una segunda pasada no cambia filas, no añade `Activity` y no
  incrementa `Revision`.
- INV-4: el barrido no toca un `Job` en `pending`, ni uno en `running` con `StartedAt` dentro de los 5 min.
- INV-5: dos barridos concurrentes sobre el mismo `Job` producen una sola `Activity` y un solo
  incremento de `Revision`.
- INV-6: la medida de salud es de solo lectura, solo del inquilino de quien la pide, y no contiene
  nombre, teléfono, texto de mensaje ni identificador de contacto, paciente o cita.

## Criterios

Todas las pruebas necesitan PostgreSQL real: se corren con `scripts/test-backend.sh "<filtro>"`.
Sin `TEST_DATABASE` el fixture lanza (`AgentIntegrationTests.cs:15`): fallan en rojo, no se saltan.
`pnpm backend:live` es de Hospital y no aplica. Los nombres de prueba son propuestos; hoy no existen.

| N.º | Criterio | Cómo se prueba |
|---|---|---|
| 1 | Al retirar un `Job` abandonado se añade una `Activity` `Kind="handoff"`, `Actor="Sistema"`, `ActorRole="system"`, con texto fijo sin datos del paciente, y `conv.Summary` toma ese texto. | Integración sobre `CrmDb`: `Job` `running` con `StartedAt` hace 6 min; se ejecuta el barrido **que invoca `AgentWorker`** (`AgentRuntime.cs:630-632`); assert de la `Activity`, de `Summary`, de `Job.Status=="uncertain"` y `conv.Status=="human"`. Si el barrido se extrae a un método (precedente: `InboxWorkflow.WakeDue`, llamado en `:629`), la prueba llama a ese método y un segundo assert comprueba que `AgentWorker.ExecuteAsync` lo llama; una prueba sobre una copia pasaría en vacío. |
| 2 | Segunda pasada del barrido: cero cambios (INV-3). | Misma prueba: se ejecuta dos veces; se cuenta `Activity` (1) y se compara `Revision` antes y después de la segunda. |
| 3 | El barrido no toca `pending` ni `running` reciente (INV-4). | Dos `Job`: `pending` y `running` con `StartedAt` hace 1 min; tras el barrido conservan estado y sus conversaciones no cambian `Status` ni `Revision`. |
| 4 | Dos barridos concurrentes retiran una vez (INV-5). | Dos `CrmDb` sobre la misma base (patrón del fixture, `options`), ambos barridos con `Task.WhenAll`; assert de una `Activity` y `Revision` +1. Hoy fallaría: `:630-632` carga y guarda sin condición. |
| 5 | Dos reclamos concurrentes del mismo `Job` `pending`: gana uno. Es regresión del comportamiento actual. | Dos `CrmDb`, el `UPDATE` condicional de `:634` en paralelo; la suma de filas afectadas es 1. Debe ejercitar la sentencia que usa `AgentWorker`, no una reescrita en la prueba. |
| 6 | Un `Job` retirado no se vuelve a ejecutar (INV-1). | Tras el criterio 1, se ejecuta la selección de `:633` y no devuelve ese `Job`; `AgentRuntime.Run` no se invoca (fake de modelo y Kapso con contador en 0, como `never` en `AgentIntegrationTests.cs:292`). |
| 7 | `GET /api/jobs/health` (solo administrador, junto a `CrmEndpoints.cs:93`) devuelve del agente: `pending`, `oldestPendingSeconds`, `runningPastDeadline` (`running` > 5 min), `uncertain24h`, `failed24h`. | Integración: se siembran filas en cada estado y se comparan los cinco números. La consulta va sobre `CrmDb.Jobs` con el filtro de inquilino de `Models.cs:247`. |
| 8 | El mismo endpoint devuelve de recordatorios: `overduePending` (`pending` con `DueAt` < ahora − 15 min), `sendingPastDeadline` (`sending` con `AttemptedAt` < ahora − 10 min), `uncertain24h`, `lastSyncAt`, `paused` (`!RemindersEnabled` o `REMINDERS_SEND_ENABLED != "true"`). | Integración sobre `CrmDb.AppointmentReminders` y `Tenants`; filas sembradas a cada lado de cada umbral. Los umbrales son los literales de `AppointmentReminders.cs:44` y `:91`. |
| 9 | La medida no cruza inquilinos y no la lee quien no es administrador (INV-6). | Filas en dos inquilinos; con `TenantScope` del primero los conteos ignoran al segundo. Llamada con rol no administrador: 403 (`RequireAdmin`, como `:93`). |
| 10 | La respuesta no contiene datos de persona (INV-6). | Contacto sembrado con nombre y teléfono centinela (patrón `PRIVATE PATIENT SENTINEL`, `AgentIntegrationTests.cs:302`); `Assert.DoesNotContain` sobre el JSON crudo, y se comprueba que solo hay números, booleanos y una fecha. |

## Anti-criterios

- Devolver a `pending` un `Job` o un recordatorio `uncertain`, o añadir intentos o lease. Contradice
  INV-6 de `backlog.md` y espera a B1.
- Enviar un mensaje al paciente desde el barrido de abandono. Hoy no se envía; se queda así hasta OQ-1.
- Añadir tabla, columna o índice. Es migración y necesita aprobación; el conteo sin índice es el techo aceptado.
- Introducir una interfaz de cola, un despachador común a los dos workers o `SKIP LOCKED`.
- Poner un límite al barrido para poder reportar `unscanned`: hoy no tiene tope, no hay nada sin revisar.
- Exponer la medida en `/health/ready` o en cualquier ruta sin sesión: filtra volumen por inquilino.
- Incluir en la `Activity` o en la salud el texto del turno, el error del proveedor o el nombre del paciente.
- Debilitar `UncertainDeliveryIsNeverRetried` o `InterruptedReminderShowsUncertainDeliveryWithoutRetry`.
- Probar el barrido con una copia de su código en la prueba.

## Fuera de alcance

- Cola con lease, prioridad e intentos; `FOR UPDATE SKIP LOCKED` (S2). Bloqueada por B1 (enmienda
  a INV-6 de `backlog.md`) y B2 (migración).
- `schedule_recheck` y cualquier mensaje proactivo del agente (S3). Bloqueada por B3 (alcance
  clínico del seguimiento), B4 (plantilla y consentimiento de WhatsApp) y B5 (PHI en el motivo).
- Motivo en el `snoozed` humano (`InboxWorkflow.cs:16`).
- `unlinkedSessions`: un `Job` no abre una sesión externa cuyo id haya que enlazar; no hay equivalente.
- Pantalla para la medida de salud.
- El plazo de 2 min del barrido de recordatorios y el orden secuencial entre inquilinos.
- `CalendarWorker`, `CommercialSyncWorker` y todo Hospital.
- Asignar al doctor del canal en el abandono, como hace `Handoff()` en `:606`. Se queda como está.

## Preguntas

- OQ-1 — **bloqueante para cambiar el statu quo, no para esta entrega**. Cuando un turno se abandona,
  ¿se le dice al paciente que una persona le responderá? Hoy queda en silencio. Contesta el dueño
  del producto con criterio clínico (un mensaje de urgencia puede ser el turno abandonado). Hasta
  entonces, no se envía nada.
- OQ-2 — **asumida**: la ventana de `uncertain24h` y `failed24h` es de 24 horas, porque no existe
  marca de «conciliado» y un total histórico no dice nada.
- OQ-3 — **asumida**: el endpoint responde siempre 200 con los conteos; no hay 503 por umbral porque
  nadie ha decidido qué número es «no sano».
- OQ-4 — **asumida**: el lector es un administrador por API; la pantalla es otra entrega. Si nadie
  va a consultar el endpoint, los criterios 7-10 no entregan valor y deben esperar a la pantalla.
- OQ-5 — **asumida**: el texto de la `Activity` es «Atención automática interrumpida. Revisa el
  historial antes de responder.»; sustituye en `Summary` a lo que hubiera **cuando es el barrido
  quien pasa la conversación a una persona**.
- OQ-6 — **bloqueante para cambiar lo implementado, abierta con el propietario (clínica)**: si la
  conversación ya estaba con una persona (`human`) cuando llega el barrido —p. ej. el agente
  derivó por urgencia y el turno murió después—, ¿el aviso reemplaza el `Summary`? **Interino
  implementado: no.** Se añade la `Activity` y el `Summary` se conserva (statu quo de esa rama),
  fijado por `AbandonedTurnAlreadyWithAPersonKeepsItsSummary`. El criterio 1 e INV-2 se leen así
  hasta que se conteste. Sin prueba: la rama `closed`, donde el `Summary` sí se reemplaza.

## Estado (2026-10-04)

Entrega A (criterios 1–6) implementada, verificada y revisada. Entrega B (criterios 7–10, el
endpoint de salud) **no construida**: no tiene lector hoy (ver plan §7). Anotado por la revisión:
el barrido ahora espera el candado de la conversación, así que puede detener el bucle hasta 30 s
si otra sesión lo sostiene durante un envío (misma exposición que ya tenía `WakeDue`); dos
reinicios seguidos a mitad de turno en la misma conversación dejan dos notas.
