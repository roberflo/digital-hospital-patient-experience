# A01 — Recuperar los mensajes que el webhook no entregó

**Estado (2026-11-14):** en main, apagado (`KAPSO_RECOVERY_ENABLED`); encender espera G0, la
revisión de seguridad (OQ-A01-6) y la firma de OQ-A01-2/M-11. Dos revisiones adversarias cerradas (ver
«Revisión adversaria»); criterios 1–15 en verde en `backend.Tests/MessageRecoveryTests.cs`,
`InboxListTests.A01_7_*` y `AgentWaitingTests.A01_*` (criterios 62–64 de `reception-agent.md` tras una
recuperación). Sin migración.

Petición (`backlog.md:82`): un job por canal que pida a Kapso los mensajes desde `LastWebhookAt`.

## Lo que la petición da por hecho y no es cierto

1. **`LastWebhookAt` no marca el inicio del hueco.** Se sobrescribe con `UtcNow` en el primer webhook
   de cualquier tipo, antes de la deduplicación (`backend/WhatsAppEndpoints.cs:198`). Cuando la API
   vuelve, el primer evento borra la cota. En un canal callado apunta a días atrás sin que haya
   habido caída. Se queda como dato de diagnóstico (`ChannelDiagnostics.cs:31`); esta spec no lo lee.
2. **«Más larga que los reintentos de Kapso» son unos 50 segundos.** Tres intentos: inmediato, +10 s,
   +40 s (docs.kapso.ai, «Delivery», leído 2026-11-14). Un reinicio de despliegue ya pierde mensajes.
3. **Kapso pausa el webhook solo.** Con 40 o más entregas, 10 o más fallidas y 85 % de fallo en
   15 min, lo deja `active=false` hasta que alguien lo reactive en su panel. Desde ahí no llega nada
   aunque la API esté sana: recuperar sólo al arrancar no basta.
4. **El cliente no tiene la llamada.** `backend/Integrations/KapsoClient.cs` envía, sube, descarga y
   lee plantillas; no lista mensajes. La API existe según la documentación:
   `GET https://api.kapso.ai/meta/whatsapp/v24.0/{phone_number_id}/messages` con `direction`, `since`,
   `until`, `limit` (máx. 100) y cursor `after`. Nunca se ha llamado desde este repo. Es el riesgo
   principal (G0).

## Propósito

Un mensaje que un paciente escribió mientras el webhook no entregaba aparece en la bandeja, en su
sitio, y una persona lo ve. Hoy se pierde sin rastro.

## Lenguaje

- **Hueco**: tiempo en que Kapso tuvo mensajes que Recepción no recibió.
- **Recuperación**: una pasada, por número, que pide a Kapso los mensajes entrantes de la ventana y
  guarda los que faltan.
- **Mensaje recuperado**: el que entró por recuperación y no por webhook.
- **Ventana**: de `ahora − Tope` a `ahora − Gracia`.
- **Gracia**: lo que se espera antes de dar un mensaje por perdido, para no adelantarse a un
  reintento del webhook.
- **Tope**: lo más atrás que mira una recuperación.

## Lo que ya existe (se reutiliza)

- Camino del webhook: `Receive` (`WhatsAppEndpoints.cs:179-215`) e `Ingest` (`:216-273`). Bloqueo por
  canal (`pg_advisory_xact_lock`, `:200-201`) y por conversación (`:248-249`).
- Deduplicación en dos capas: `Receipt` por cabecera más huella (`:203-206`, barrido a 30 días) y
  `Message.ExternalId` (`:220`, `:251`), con índice único `(TenantId, ExternalId)` (`Models.cs`,
  `OnModelCreating`) que nunca se barre. La recuperación se apoya sólo en la segunda.
- Disparo del agente: `Job` con clave `agent:<wamid>` (`:270`), índice único `(TenantId, Key)`. El
  agente sólo responde si la clave es la del último mensaje del paciente (`AgentRuntime.cs:46`).
- Tareas periódicas por inquilino: `AgentWorker.RunTenant` (`AgentWorker.cs:49-52`) llama a
  `Retention.Daily` con cursor en memoria por proceso y fallo aislado (MR-39). Es el molde.
- Kapso falso en pruebas: `Fake : HttpMessageHandler` (`backend.Tests/WhatsAppOnboardingTests.cs:179`);
  webhook real contra Postgres en `backend.Tests/WhatsAppWebhookTests.cs`, con `CapturedLogs` (`:46`).
- No leídos por `ReceivedAt` (`WhatsAppEndpoints.cs:100`) y bandeja por `UpdatedAt` (`:98`): un
  mensaje recuperado cuenta como no leído y sube la conversación sin código nuevo.

## Superficie

- Sin endpoint ni pantalla nuevos.
- Una llamada nueva en `KapsoClient` sobre `Request(HttpMethod.Get, "{number}/messages?…")`
  (`KapsoClient.cs:168`), con `KAPSO_API_KEY`.
- Un paso periódico en `AgentWorker.RunTenant`, junto a `Retention.Daily`.
- Configuración: `KAPSO_RECOVERY_ENABLED` (por defecto apagado).

## Forma y sitio del dato

**Sin migración.** No hay tabla ni columna nueva. El mensaje recuperado es una fila `Message` como la
del webhook: cuerpo cifrado, `CreatedAt` con la hora de WhatsApp, `ReceivedAt` con la de ingreso. El
cursor vive en memoria por proceso (precedente `Retention.swept`); al reiniciar se mira el Tope
entero y `ExternalId` absorbe lo repetido.
`// ponytail:` cursor en memoria; una columna si las pasadas de Tope completo al arrancar cuestan.

## Qué la ata

- `reception-agent.md` INV-4 (identidad e inquilino los fija el servidor) e INV-8 (el log no lleva
  teléfono, cuerpo ni id de mensaje).
- `mejoras-revision-2026-10.md` MR-39 (una tarea de mantenimiento nunca detiene el turno) y MR-47
  (qué es un traspaso visible).
- `Hospital/AGENTS.md` «Qué despierta a una persona» n.º 5: toca PHI, así que pasa por
  `security-auditor` antes de encenderse.

## Invariantes

- INV-1 (idempotencia de mensaje): un `wamid` produce como mucho una fila `Message` por inquilino,
  llegue por webhook, por recuperación, por ambos en cualquier orden o por dos recuperaciones.
- INV-2 (el agente no habla por un mensaje recuperado): la recuperación nunca crea un `Job`, y tras
  ella ningún envío sale por ese mensaje.
- INV-3 (nadie cree que el agente atiende): tras recuperar un mensaje, su conversación queda en
  `Status = human`, `State = open`, con una sola `Activity` de «Sistema» por conversación y pasada:
  `handoff` si estaba en `agent` o `closed`; `recovered` (mismo texto, OQ-A01-7) si ya estaba en `human`,
  para no cerrar la espera que abrió un traspaso del agente (`reception-agent.md` criterios 62–64).
  *Enmendado 2026-11-14 tras la revisión adversaria (I-1).*
- INV-4 (orden): el mensaje recuperado se guarda con la hora de WhatsApp. `LastInboundAt` sólo
  avanza. `LastMessage` no cambia si la conversación ya tiene un mensaje posterior.
- INV-5 (gracia): no se recupera un mensaje más joven que la Gracia; ese sigue siendo del webhook y
  dispara al agente como hoy.
- INV-6 (tope): ninguna recuperación guarda mensajes anteriores a `ahora − Tope`. Una ventana nueva
  tampoco los pide; una ventana que se reanuda con el cursor `after` de Kapso conserva su `since`
  original, que puede quedar unos minutos detrás del Tope (G0.3). *Reformulado 2026-11-14 (M-1).*
- INV-7 (aislamiento): una recuperación corre dentro del inquilino del canal y sólo escribe filas
  suyas.
- INV-8 (sin efectos laterales): un mensaje recuperado no cambia el consentimiento de recordatorios
  ni confirma, cambia o cancela una cita.
- INV-9 (fallo aislado): un fallo de Kapso no impide el turno del agente de ese inquilino ni avanza
  el cursor.
- INV-10 (un solo camino): contacto, conversación, tipo, medio y cuerpo de un mensaje recuperado se
  derivan con el mismo código que el webhook.

## Puerta G0 — contrato real de Kapso (antes de encender, no antes de construir)

Una llamada real a `GET {number}/messages?direction=inbound&since=…&until=…&limit=100&fields=kapso()`
con un número de pruebas, hecha por quien tenga `KAPSO_API_KEY` del entorno. Debe contestar:

1. ¿Trae `kapso.phone_number`, `kapso.contact_name`, `kapso.content`, `kapso.origin`, `kapso.direction`
   y `timestamp`?
2. ¿`since` y `until` filtran por la hora de WhatsApp o por la de alta en Kapso?
3. ¿En qué orden pagina, y `after` termina con cursor ausente? ¿Sigue valiendo un cursor `after` en
   la pasada siguiente, con el mismo `since` y `until`? (la reanudación de M-1 y M-9 lo da por hecho)
   ¿Puede devolver el mismo `after` que se le pidió? Si lo hace, hoy la ventana se cierra con un aviso
   (M-10) y lo que quedara detrás de ese cursor no se pide.
4. ¿Hay límite de frecuencia?
5. ¿Aparecen los mensajes cuyo webhook falló o se omitió por pausa?

La 5 decide si A01 sirve; si es «no», se cierra sin encender. Mientras tanto las pruebas usan el
ejemplo de la documentación como fixture, marcado «sin confirmar».

## Stories

- **S1 — Lo perdido llega a una persona.** Criterios 1–15. Entregable sola.
- Fuera de esta spec, por nombre: S2 (el agente contesta un mensaje recuperado reciente; OQ-A01-2),
  S3 (ecos salientes y estados perdidos; OQ-A01-5).

## Criterios

Pruebas de backend en un fichero nuevo `backend.Tests/MessageRecoveryTests.cs`, contra Postgres con
`scripts/test-backend.sh` (montaje de `WhatsAppWebhookTests`), con Kapso falso y «ahora» inyectado.

| N.º | Criterio | Cómo se prueba |
|---|---|---|
| 1 | El cliente lista los entrantes de un número entre dos instantes y recorre todas las páginas. | Sobre el método nuevo de `KapsoClient` con `Fake`: ruta `/meta/whatsapp/v24.0/{number}/messages`, `direction=inbound`, `since`, `until`, `limit=100`, cabecera `X-API-Key`; con `paging.cursors.after` en la primera respuesta hay segunda petición con `after`; sin cursor, termina. Un 500 lanza `HttpRequestException`. |
| 2 | Un mensaje que Kapso tiene y Recepción no queda guardado como lo habría guardado el webhook (INV-10). | El mismo mensaje sintético por el delegado `/webhooks/kapso` en un inquilino y por recuperación en otro: mismas `Body`, `Type`, `MediaId`, `Sender`, `ExternalId`, `CreatedAt`; contacto y conversación creados si no existían; `IsHistory == false`. |
| 3 | Recuperar dos veces no duplica (INV-1). | Dos pasadas con la misma respuesta: un `Message`, una `Activity` `handoff`; la segunda no añade filas ni sube `Revision`. |
| 4 | Webhook y recuperación del mismo mensaje, en cualquier orden, dejan uno (INV-1). | (a) recuperación y luego webhook firmado: un `Message`, cero `Jobs`. (b) webhook y luego recuperación: un `Message`, un `Job` (el del webhook), `Status` de la conversación intacto, sin `handoff`. |
| 5 | La recuperación nunca dispara al agente (INV-2). | Tras recuperar, `Jobs` vacío. Luego `AgentWorker.RunTenant` con `SEND_ENABLED=true`, agente y canal encendidos: el `Fake` cuenta cero `POST …/messages`. |
| 6 | La conversación queda con una persona y lo dice (INV-3). | Tres casos de partida: `agent`; `closed` sin responsable con agente encendido (el caso en que `InboxWorkflow.ReopenOnInbound` la devolvería al agente, `InboxWorkflow.cs:27-33`); y `human`. En los tres: `Status == "human"`, `State == "open"`, una `Activity` `handoff` de `ActorRole == "system"` con el texto fijo. Tres mensajes de una conversación en una pasada: una sola. *Enmendado 2026-11-14 (I-1):* en el caso `human` esa actividad es `recovered`, no `handoff`; y tras recuperar en una conversación derivada por el agente sin respuesta, una emergencia en vivo sigue recibiendo el teléfono (`AgentWaitingTests.A01_*`). |
| 7 | La persona lo ve en la bandeja. | Lectura de la lista de conversaciones (`WhatsAppEndpoints.cs:96-111`): `UnreadCount` cuenta el recuperado y la conversación es la primera de su prioridad. Añadir a `backend.Tests/InboxListTests.cs`. |
| 8 | El orden de la conversación es el real (INV-4). | Conversación con un mensaje en vivo de las 12:00; se recuperan dos de las 10:00 y 10:05. Leídos por `WhatsAppEndpoints.Messages` salen 10:00, 10:05, 12:00; `LastMessage` sigue siendo el de las 12:00; `LastInboundAt` no retrocede. |
| 9 | No se adelanta al webhook (INV-5). | La petición lleva `until == ahora − Gracia`. Un elemento con `timestamp` más joven que la Gracia no se guarda; su webhook posterior crea `Message` y `Job` como hoy. |
| 10 | No mira más atrás del Tope (INV-6). | Primera pasada del proceso: `since == ahora − Tope`. Un elemento con `timestamp` anterior no se guarda. Segunda pasada: `since` es el `until` anterior menos el solape, no el Tope. |
| 11 | Una ventana grande se completa, no se trunca. | 250 mensajes en páginas de 100: tras las pasadas necesarias hay 250 `Message`; si una pasada se detiene por su límite de páginas, el cursor no avanza más allá de lo guardado. |
| 12 | Un mensaje recuperado no toca consentimiento ni citas (INV-8). | Recuperado con cuerpo «BAJA» para un contacto con `ReminderConsentAt` no nulo: sigue no nulo y no hay `Activity` `reminder_consent`. Recuperado con el payload de «Confirmar» de un recordatorio: el `AppointmentReminder` no cambia y no hay llamada a Hospital. |
| 13 | Si Kapso falla, el agente sigue (INV-9). | `Fake` con 500 y con espera agotada: `AgentWorker.RunTenant` no lanza, un `Job` pendiente de ese inquilino termina `done` en la misma llamada, y la pasada siguiente repite el mismo `since`. Se registra un aviso con el tipo de error. |
| 14 | El registro no lleva datos del paciente. | `CapturedLogs` (`WhatsAppWebhookTests.cs:46`): tras recuperar y tras fallar, ninguna línea contiene el teléfono, el cuerpo ni el `wamid` sintéticos; sí inquilino, canal y conteo. |
| 15 | Apagado no hace nada; cada inquilino, lo suyo (INV-7). | Sin `KAPSO_RECOVERY_ENABLED=true`: cero llamadas del `Fake`. Un canal cuyo `PhoneNumberId` empieza por `demo` no se consulta (precedente `ChannelDiagnostics.cs:15`). Dos inquilinos: recuperar el A deja cero filas con `TenantId` de B. Un elemento `kapso.direction == "outbound"` u `origin == "history_sync"` no se guarda. |

**Huecos declarados:** (a) la forma real de la respuesta de Kapso: los criterios 1, 2 y 15 pasan
contra el ejemplo documentado hasta G0. (b) Nada prueba que Kapso conserve los mensajes de un webhook
fallido: es G0.5. (c) El audio recuperado puede venir sin transcripción; se guarda como hoy se guarda
un audio sin ella.

## Revisión adversaria (2026-11-14)

Corregido: I-1 (la nota de un mensaje recuperado en una conversación ya `human` es `recovered`, no
`handoff`: `HandedOverUnanswered` no la ve y la espera de 62–64 sigue abierta); M-1 (la ventana
reanudada conserva su `since`; INV-6 se impone al guardar); M-2 (una marca de tiempo fuera de rango,
p. ej. en milisegundos, se salta y se cuenta con la misma comprobación que `Ingest`); M-4 (cada pasada
tiene un presupuesto total de 10 s: un Kapso colgado no retiene el turno del inquilino más de eso);
M-6 (C5 con el `Job` del arnés vivo, estado `read` en el fixture, `fields=kapso()` comprobado). Nits:
la comprobación «toca pasada» va en memoria antes de leer `Channels`; el interruptor se lee con
`bool.TryParse`.

Documentado, sin cambio de código:

- **M-3.** La Gracia se mide sobre la hora de WhatsApp (`timestamp`), no sobre la llegada a Kapso. Si
  Kapso recibe tarde un mensaje (p. ej. un teléfono sin red), puede tener más de 3 min de WhatsApp y
  seguir dentro de los reintentos del webhook: la recuperación y el webhook se cruzan y `ExternalId`
  deja una fila, pero el mensaje va a una persona sin agente. Depende de G0.2.
- **M-5.** Un mensaje recuperado con `kapso.passive = true` en una conversación del agente deja dos
  actividades (la de atención externa y la de «Sistema»). Con varias réplicas cada proceso hace su
  pasada y puede dejar su propia nota; `ExternalId` y el bloqueo del canal siguen impidiendo filas
  `Message` repetidas.
- **Nit 3.** Una conversación creada por la recuperación queda con `ExternalId = ""`: el listado no
  trae el id de conversación de Kapso donde el webhook lo trae (`conversation.id`).
- **Segunda revisión (2026-11-14), corregido.** M-9: una pasada cortada por el presupuesto guarda
  `(since, until, after de la última página completa)` y la siguiente sigue desde ahí; los números se
  recorren en orden estable y la pasada siguiente empieza por el que quedó detrás del cortado, así que
  ninguno se queda sin turno detrás de uno colgado. M-10: un `after` igual al pedido cierra la ventana y
  deja un aviso sin datos del paciente. Pruebas de 62 y 63 tras una recuperación
  (`AgentWaitingTests.A01_after_*`), con rojo por mutación.
- **Nit `AgentMetrics`.** Sus `kinds` contarán `recovered` y los `handoff` de «Sistema» que deja la
  recuperación; no son traspasos del agente.
- **Despliegue.** Al encenderla, el dueño añade `KAPSO_RECOVERY_ENABLED` a la documentación de
  despliegue; hoy no figura porque está apagada.

## Anti-criterios

- Usar `LastWebhookAt` como cota, o añadir una columna de cursor «para hacerlo bien».
- Una segunda implementación de la ingesta, o crear `Receipt` para mensajes recuperados.
- Crear el `Job` «sólo si el mensaje es reciente»: eso es S2 y espera OQ-A01-2.
- Enviar al paciente un aviso, una disculpa o el teléfono de emergencias por un mensaje recuperado
  (OQ-A01-3).
- Aplicar BAJA o ALTA desde un mensaje recuperado (OQ-A01-4).
- Reactivar el webhook pausado desde el código, o consultar el número general de la plataforma.
- Pedir sin `direction=inbound`, o guardar ecos y estados (S3).
- Registrar teléfono, cuerpo o `wamid` en log, traza o `Job.Error`.
- Un `catch` que avance el cursor tras un fallo.
- Encenderlo en producción sin G0.
- Debilitar `SignedInboundCreatesOneMessageAndOneJobAndARedeliveryAddsNothing` o
  `ARedeliveryAfterTheReceiptIsSweptStillAddsNothing`.

## Fuera de alcance, por nombre

S2; S3; el número general (`/webhooks/kapso/general`, `GeneralNumber.cs`), cuyo BAJA perdido es un
hallazgo de cumplimiento para S4 de recordatorios; un aviso en `/health/attention` de «webhook
pausado en Kapso»; A03; persistir el cursor.

## Preguntas

- **OQ-A01-1 — asumida.** Gracia de 3 min, Tope de 24 h, pasada cada 5 min por número, solape de una
  Gracia. La Gracia cubre los 50 s de reintentos más los 30 s de orden y hasta 60 s de agrupación de
  Kapso; el Tope iguala la ventana de respuesta de WhatsApp (`Rules.WithinWindow`, `Models.cs:358`).
  Son constantes con comentario, no configuración. G0.4 puede obligar a espaciar la pasada.
- **OQ-A01-2 — asumida para S1; bloqueante para S2 (dueño, con el médico responsable para la parte de
  urgencias).** Asunción: ningún mensaje recuperado dispara al agente; todos van a una persona. Coste
  que el dueño debe conocer: tras una caída nocturna de más de 50 s esas conversaciones quedan en
  `human`, y el agente no las retoma aunque el paciente vuelva a escribir, hasta que alguien pulse
  «Activar agente». Lo que hay que decidir para S2: hasta qué antigüedad puede contestar el agente, y
  qué hace con un mensaje tardío que nombra una urgencia.
- **M-11 — bloqueante para encender — lo firman el dueño y el médico responsable** (2026-11-14, bajo
  OQ-A01-2). En una conversación que estaba en `agent` o `closed`, tras un mensaje recuperado queda en
  `human` con un `handoff` de «Sistema», que no abre la espera de los criterios 62–64: un mensaje
  posterior **en vivo** que nombre una emergencia no recibe el teléfono de emergencias automático hasta
  que alguien pulse «Activar agente». En una que ya esperaba tras un traspaso del agente sí lo recibe
  (I-1). Sin cambio de código en S1.
- **OQ-A01-3 — bloqueante para ese aviso; no bloquea S1 (clínica: dueño + médico responsable).** ¿Debe
  el paciente recibir algo automático cuando su mensaje se recupera tarde? Statu quo congelado: no se
  envía nada.
- **OQ-A01-4 — bloqueante para aplicar consentimiento; no bloquea S1 (dueño + `compliance-auditor`).**
  ¿Un BAJA o ALTA recuperado cuenta, y qué pasa si hay uno posterior en vivo? Statu quo congelado: no
  cambia el consentimiento (INV-8); la persona lo ve y lo aplica desde la ficha
  (`ReminderEndpoints.cs:103`). Riesgo escrito: hasta que lo haga, un recordatorio puede salir a quien
  pidió BAJA. Debe cerrarse antes de encender recordatorios con pacientes reales.
- **OQ-A01-5 — asumida.** S1 recupera sólo entrantes. Un eco perdido del teléfono del doctor en un
  número con coexistencia sigue perdido; como S1 deja toda conversación tocada en `human`, no hay
  agente que conteste encima.
- **OQ-A01-6 — bloqueante para encender; no para construir (`security-auditor`).** Revisión de la
  lectura nueva de PHI: mismo proveedor, misma clave y mismo cifrado en reposo que el webhook; lo
  nuevo es que Recepción inicia la lectura y que los cuerpos pasan por memoria en cada pasada.
- **OQ-A01-7 — asumida.** Texto de la nota: «Mensaje del paciente recuperado tras una interrupción de
  la conexión con WhatsApp. El agente no respondió: revisa y contesta.»
