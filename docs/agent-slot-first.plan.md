# Plan — La hora antes que el registro (S3)

Spec: [agent-slot-first.md](agent-slot-first.md). Medido el 2026-10-04, solo lectura: no se
ejecutó ninguna prueba.

> **Implementado (2026-10-04).** Lo que el código hace distinto de este plan:
> - «Corregir datos» y «Otro horario» **borran** la fila de la propuesta en vez de consumirla
>   (`proposal_used:`): esa tarjeta nunca se confirmó y el feed la mostraría como procesada.
> - Un formulario vencido borra **todas** las filas `intake` de la conversación, no solo la última.
> - `booking` es `action is "create" or "reschedule" || (register con startsAt)`.
> - Tras la revisión adversaria: el «sí» escrito solo confirma si la tarjeta es lo último que dijo
>   el agente; `Confirm` captura `ArgumentException`; un toque de horario sin expediente valida
>   duración y doctor; un comando del menú a mitad del formulario lo cierra; la referencia del
>   expediente va en el aviso al equipo si la verificación falla; el comprobador de INV-T-2 cubre
>   también «aparté/reservé».
> - Verificado sobre `main` + S3: suite, los dos recorridos en vivo sin modelo contra el Hospital
>   local, y evals con `gpt-6-luna` (109/111 antes y después; los 13 casos de registro y
>   `urgencia-004` pasan). Sin correr: el recorrido en vivo con modelo (solo NIM).

**Estado: implementable sin migración, con una precondición de secuencia y un hueco de la spec.**

- **Precondición:** S1+S2 (`agent-intake-voice`) son la base. S3 se construye encima de ese diff.
- **Hueco de la spec:** T-1 dice reemplazar una prueba, pero el cambio de «Agendar cita» obliga a
  remontar nueve (§4).

## 0. Estado del árbol

- `main` local de Recepción: `4e05f77`; `origin/main`: `6ff1cfd`.
- Trabajo ajeno sin confirmar que compila en la misma corrida y no se toca:
  `backend/HospitalEndpoints.cs`, `backend/Integrations/HospitalClient.cs`, `backend/Program.cs`,
  `backend/InstallationEndpoints.cs`, dos archivos de prueba sin seguimiento y el frontend.
- Todas las líneas citadas por la spec siguen vigentes en `4e05f77`: `:53`, `:98`, `:296-302`,
  `:298`, `:300`, `:417`, `:427-440`, `:468-478`, `:532-566`, `:534`, `:584-622`, `:590`, `:602`,
  `:606`, `:621`, `:628`, `:634`, y `HospitalClient.Agent.cs:14-19`. El diff de S1+S2 solo
  reemplaza en sitio `:145` y `:634` de `AgentRuntime.cs`: la numeración no se mueve.
- Base real de S3: `4e05f77` más el diff de S1+S2 (`Data()`, `Next()`, los textos nuevos y sus
  pruebas).
- Los nombres de prueba citados por la spec existen.

Erratas de `docs/reception-agent.md` (nombres que no existen en el código):

| Criterio | Cita | Nombre real |
|---|---|---|
| 32 | `MenuForSomeoneWithoutRecordGoesToTheAgent` | `MenuForSomeoneWithoutRecordStartsTheRegistrationForm` |
| 32 | `MenuWithNothingToOfferSaysSoAndReachesReception` | `MenuWithNothingToOfferSaysSoAndOffersReception` |
| 18 | `PossibleDuplicateIsNeverForcedAndGoesToReception` | `PossibleDuplicateIsNeverForcedAndReceptionIsOffered` |
| 35 | `AMinorInTheFormGoesToReception` | `AMinorInTheFormIsOfferedReception` |

### Cómo se corre

- Con cwd en la raíz del worktree: `scripts/test-backend.sh "<filtro>"`. Nunca `~Agent` a secas.
- Filtro de trabajo:
  `FullyQualifiedName~AgentSlotFirstTests|FullyQualifiedName~AgentInteractionTests|FullyQualifiedName~AgentIntakeFlowTests|FullyQualifiedName~AgentIntakeTests|FullyQualifiedName~AgentTraceTests|FullyQualifiedName~AgentVoiceTests|FullyQualifiedName~AgentAppointmentsTests|FullyQualifiedName~AgentWaitingTests|FullyQualifiedName~AgentMemoryTests|FullyQualifiedName~AgentEvalCasesAreValid`
- Cierre de cada paso: sin argumento. Línea base **B** en el paso 0; final esperado **B + 21**.

## 1. Decisiones de forma

### Dónde vive la hora elegida

Un campo más en `Intake`: `string? Slot = null`, al final del record. Guarda el texto tocado tal
cual (`CITA <inicio> <doctor> <minutos> <nombre>`) y se relee con `AgentGuard.SlotChoice`.

- Sin tabla, sin migración, sin tipo nuevo. Un `intake` en curso de antes del despliegue se lee
  con `Slot == null`. `Data()` enumera los siete datos a mano, así que `Slot` no cuenta como dato.
- **INV-T-5 se cumple:** la hora vive y muere en la fila `intake`, ya excluida de
  `/api/activities` (`CrmEndpoints.cs:65`) y del feed (`ActivityFeed.cs:48`).
- **T-11 se cumple a medias en el plazo de 30 minutos.** Hoy un formulario vencido no se borra,
  solo se ignora (`:91` filtra por fecha). El plan lo borra cuando el paciente vuelve a escribir,
  no por reloj (P-2).

### La tarjeta única

**`register` con cita opcional, no una tercera acción.** El payload de `register` gana las cuatro
claves que ya usa `create`: `startsAt`, `doctorId`, `durationMinutes`, `doctorName`. `Confirm` ya
trata `register` aparte en `:590` (sin `RequirePatient`) y `:592` (borra los datos al consumir).
Código, vencimiento de 15 minutos, consumo antes de escribir y el «sí» de `:85-89` no cambian.

### Orden en Confirmar

Forma objetivo de `Confirm` tras consumir la propuesta (`:592`), dentro del `try`:

```csharp
var booking = action != "cancel" && p.TryGetProperty("startsAt", out _);
// 1. solo lectura: un fallo aquí no ha escrito nada
if (booking) free = start > DateTimeOffset.UtcNow && await hospital.IsSlotAvailableAsync(...);
var registered = action == "register";
if (registered)
{
    // 2. POST /v1/patients  3. verificar y vincular. false = duplicado o ya vinculado: no pasa nada más
    if (!await Register(conv, contact, p, booking ? wanted : null, ct)) return;
    if (!booking || !free) { await OfferSlots(true, null, ct, booking ? "Ya te registré. Ese horario se acaba de ocupar.\n" : $"Listo{nombre}, ya te registré.\n"); return; }
    action = "create";
}
else if (booking && !free) { /* el OfferPerson de :602, sin cambio */ return; }
// 4. rama create/reschedule/cancel existente (:596-619), sin duplicar
```

- `Register` pasa a devolver `Task<bool>`, recibe `wanted` (fecha, hora y doctor) para el motivo
  del duplicado (T-8) y deja de llamar a `OfferSlots`.
- **INV-T-4:** la propuesta ya se consume antes de cualquier escritura.
- **INV-T-3:** el POST de cita solo es alcanzable si `Register` devolvió `true` en ese turno.
- `first-visit` sale sin tocar `:606`: `patient_registered` se acaba de guardar.
- Mensaje final cuando `registered`: «Listo, Ana, ya te registré y tu cita quedó agendada:\n*<fecha
  y hora>*\n\n¡Te esperamos!», con el botón existente.
- `catch` de `:621`: cuando `registered`, el motivo para el equipo dice qué quedó hecho y la hora
  deseada (A-2).
- **Efecto lateral deliberado de `start > UtcNow`:** hoy una hora ya pasada al confirmar lanza
  `ArgumentException` desde `HospitalClient.cs:304`, que `:621` no captura, y el trabajo acaba en
  `failed`. S3 lo hace más probable (formulario de 30 minutos más tarjeta de 15). Con la guarda
  cuenta como «ocupado», también para quien tiene expediente.

Peticiones que verá el Hospital simulado en T-6, en orden: `GET booking-options`,
`POST /v1/patients`, `GET /v1/patients/{id}`, `GET /v1/patients/{id}`, `POST /v1/agenda`.

### Tocar un horario sin expediente (`ProposeTapped`, `:296-302`)

`:298` se reemplaza por una rama dentro del `try` existente, para que el
`catch (ArgumentException)` de `:300` sirva a T-4 con el mismo texto.

1. Validar inicio futuro con zona. La condición de `:542` se extrae a un ayudante que devuelve
   `DateTimeOffset?`; `Propose` hace `?? throw`.
2. Si hay exactamente una propuesta `register` viva en la conversación: consumirla y reemitir la
   tarjeta con sus datos y la hora nueva (T-13, «Otro horario»).
3. Si no: `Intake.Start(raw, When(start))`, `Trace("start_registration")`, guardar `intake`,
   enviar la pregunta.

- Ninguna llamada a Hospital en esta rama (INV-T-1): la comprobación es al confirmar.
- Nuevo en `AgentIntake.cs`, una línea:
  `public static (...) Start(string slot, string when) => Ask(new Intake(1, Slot: slot), $"Para apartar el *{when}* necesito registrarte: son 7 datos cortos.\n\n");`
- El lead con hora reemplaza al de `Start()` solo en este camino. Las siete preguntas, «Último
  dato:», los errores y «ya te registré» se reutilizan sin cambio.

### Bloque del formulario (`:91-95`) y entrada del menú

- La consulta de `:91` pierde el filtro de 30 minutos; la condición de `:93` gana
  `reciente && AgentGuard.SlotChoice(latest.Body) is null`. Lo que no entra se borra.
- Un toque de horario a mitad del formulario lo reinicia con la hora nueva. Marcar con
  `// ponytail:`: se pierden las respuestas ya dadas; conservarlas exige texto nuevo.
- `:98-103` se **borra** y `:110` pierde `contact.PatientId is not null &&`. `OfferSlots` ya
  ofrece una persona sin horarios o con Hospital caído (T-1). `:53` no se toca.

### `IntakeStep` y `ProposeRegistration`

- `ProposeRegistration` gana `string? slot = null` tras `ct`; el tool `propose_registration` y
  `:232` no cambian. `IntakeStep` (`:476`) pasa `next.Slot`.
- Con hora válida y futura: payload con las cuatro claves, y la tarjeta cierra con
  `\n\n*Cita:* {When}\n*Con:* {doctor}\n\n¿Confirmo tu registro y tu cita?`. Se aplica el doctor
  fijo del canal igual que `:546`.
- Si la hora ya pasó al completar el formulario, sale la tarjeta de registro de siempre.

### Vía del modelo (T-2, T-15)

- `Availability` (`:416-417`): se borran las dos condiciones sobre `contact.PatientId`.
- **Excepción que la spec no vio:** con `forOther` y sin expediente, la lista no se llena. Sin
  ella, «una cita para mi papá» recibiría la lista, la tocaría y el formulario registraría a un
  tercero con el teléfono de quien escribe.
- `Propose` (`:534`): para `create` sin expediente devuelve un error instructivo («pídele que
  toque un horario de la lista; no derives») en vez de lanzar `RequirePatient`.
- Prompt `:145-149`, propuesta: «Cliente sin expediente que quiere una cita: consulta
  hospital_availability como con cualquier paciente; el sistema adjunta la lista y, cuando toca un
  horario, le pide sus datos y le envía una sola propuesta con su registro y su cita. No le pidas
  datos antes de que elija horario ni uses propose_action con él. Usa start_registration solo si
  pide registrarse sin pedir cita o si ya escribió sus datos…». El resto sigue igual.
- **Sin puerta:** que el modelo obedezca el prompt, y la hora escrita en vez de tocada por alguien
  sin expediente.

### T-13: coste real

Barato con este diseño (unas 15 líneas, sin estado nuevo). **No se parte S3.**

- **«Otro horario»** lleva el id `AGENDAR`, que ya es comando: cae en `:110`, muestra la lista, y
  el toque entra en el punto 2 de `ProposeTapped`. Cero manejadores nuevos.
- **«Corregir datos»** lleva `CORREGIR <código>`: una alternativa más en
  `WhatsAppContent.Command()`; un despacho junto a `CONFIRMAR` (`:78`); un método que consume la
  propuesta, borra sus datos y abre `Intake.Start(raw, When(start))`.
- Si el paso 5 se cae, la tarjeta sale con dos botones como hoy. No queda botón muerto.

## 2. Archivos que cambian

| Archivo | Cambio |
|---|---|
| `backend/AgentIntake.cs` | campo `Slot`; sobrecarga `Start(slot, when)` |
| `backend/AgentRuntime.cs` | `:78` (despacho `CORREGIR`), `:91-95`, `:98-103` (borrar), `:110`, `:145-149`, `:292`, `:296-302`, `:416-417`, `:453-465`, `:476`, `:534`/`:542`, `:584-622`, `:623-635` |
| `backend/WhatsAppContent.cs` | `CORREGIR [0-9A-F]{6}` en `Command()` |
| `backend.Tests/AgentSlotFirstTests.cs` | **nuevo**: T-3…T-9, T-11…T-13, con un Hospital simulado que anota método y ruta en orden |
| `backend.Tests/AgentInteractionTests.cs` | T-1, T-2, un `InlineData` |
| `backend.Tests/AgentIntakeFlowTests.cs` | montajes (§4), T-10, prueba de `forOther` |
| `backend.Tests/AgentTraceTests.cs`, `AgentVoiceTests.cs` | montajes (§4) |
| `backend.Tests/AgentMemoryTests.cs` | prueba del prompt |
| `backend.Tests/AgentLiveJourney.cs` | `:55`, `:177-190`; a ciegas, es `Category=Live` |
| `evals/agent/cases/urgencia-004-cliente-nuevo-de-noche.json` | la expectativa pasa a `hospital_availability` y lista; solo se valida su forma |
| `docs/reception-agent.md` | criterios 26, 32, 34, 35, 36 y los cuatro nombres de §0 |

No se tocan `AgentGuard.cs`, `HospitalClient*`, `Models.cs`, `Migrations/`, `ActivityFeed.cs`,
`CrmEndpoints.cs`, `ConversationService.cs`, `AgentWorker` ni el frontend.

## 3. Orden del trabajo

El cambio de la entrada va **después** de que todo lo que hay detrás funcione. Antes del paso 4
nada cambia para el paciente por botones: los pasos 1 a 3 solo se alcanzan escribiendo `CITA …` a
mano, que es como las pruebas los conducen.

| Paso | Qué | Conteo |
|---|---|---|
| 0 | Línea base, suite sin argumento | **B** |
| 1 | Guardar la hora: `Slot`, `Start(slot, when)`, rama sin expediente de `ProposeTapped` (punto 2 no), `:91-95`. Cubre T-3, T-4, T-10, T-11 | B + 6 |
| 2 | Tarjeta única: `ProposeRegistration` con hora, `IntakeStep`. Cubre T-5, T-12 | B + 8 |
| 3 | **Confirmar**: `Confirm`, `Register`. Cubre T-6…T-9. **No desplegar entre el paso 2 y el 3**: la tarjeta pregunta por registro y cita, y Confirmar solo registraría | B + 13 |
| 4 | Cambiar la entrada: borrar `:98-103`, `:110`, `Availability`, excepción `forOther`, montajes de §4. Cubre T-1, T-2 | B + 16 |
| 5 | Botones: `CORREGIR`, punto 2 de `ProposeTapped`, tres botones. Cubre T-13 | B + 19 |
| 6 | Prompt, error instructivo de `Propose`, JSON de eval, `AgentLiveJourney.cs`, documentos. Cubre T-15 | **B + 21** |

En cada paso: rojos primero con el mensaje pegado, verde con el filtro, y luego la suite completa.

### Orden entre planes

1. `agent-jobs-health`: independiente; `wt-jobs` debe rebasar sobre `4e05f77`. No choca con S3.
2. `agent-intake-voice` S1 y S2: antes de S3.
3. **S3.**
4. `agent-evidence`: después de S3 y en serie. Choca textualmente en `AgentIntake.cs` y
   `AgentIntakeFlowTests.cs`; en asserts, no.

## 4. Asserts y montajes existentes que cambian

Nueve pruebas abren el formulario con `AGENDAR` sin expediente, la mayoría sin Hospital simulado.
Tras el paso 4, `AGENDAR` consulta la agenda. Ningún assert se borra ni se debilita.

| # | Prueba | Cambio | Por qué |
|---|---|---|---|
| 1 | `AgentInteractionTests.MenuForSomeoneWithoutRecordStartsTheRegistrationForm` | reemplazada por T-1 | enmienda del criterio 32 |
| 2 | `AgentInteractionTests.ContactWithoutRecordGetsNoTappableSlots` | invertida y renombrada (T-2) | enmienda del criterio 26 |
| 3 | `AgentIntakeFlowTests.ALabelledEmergencyContactInTheFormIsNotAnEmergency` | primer mensaje: un `CITA …` futuro | prueba el formulario, no su entrada |
| 4 | `AgentIntakeFlowTests.AQuestionInTheMiddleOfTheFormIsNotTakenAsAnAnswer` | igual; `model.Calls` no cambia | ídem |
| 5 | `AgentTraceTests.RegistrationFormInProgressNeverShowsInTheHistory` | igual, más un assert: ningún item del feed contiene el `startsAt` | criterio 50 e INV-T-5 |
| 6 | `AgentTraceTests.TheFormThanksByName` | igual | ídem |
| 7 | `AgentIntakeFlowTests.AMinorInTheFormIsOfferedReception` | igual, más asserts de T-10 | se convierte en T-10 |
| 8 | `AgentIntakeFlowTests.NewClientTapsAgendarRegistersStepByStepAndSeesTheFreeHoursWithoutTheModel` | el primer turno pasa a `ToolCall("start_registration", new { })`; renombrar a `NewClientRegistersStepByStepAndSeesTheFreeHours` | pasa a cubrir los criterios 35 y 36 para quien se registra sin hora (T-14) |
| 9 | `AgentVoiceTests.EveryStepOfRegisteringReadsLikeAChat` | igual que la fila 8 | conserva `EndsWith("¿Están correctos?")` |
| 10 | `AgentTraceTests.RegisteringNamesTheContactAndPointsToTheHospitalRecord` | igual que la fila 8 | conserva `StartsWith("Listo, Ana")` |
| 11 | `AgentInteractionTests.TappedChoiceReadsAsTheCommandOrItsLabel` | un `InlineData` más: `CORREGIR 1A2B3C` | adición |
| 12 | `AgentLiveJourney.cs:55`, `:177-190` | recorrido nuevo: lista, toque, formulario, una tarjeta | sin verificar |

**Sin tocar y en verde (T-14 y anti-criterios):** `AgentHandsTheFormWhatItAlreadyKnows`,
`NewClientIsRegisteredOnlyAfterConfirmingAndCanThenBook`,
`FirstAppointmentOfARegisteredClientIsAFirstVisit`, `StaleOrMalformedSlotIsNeverProposed`,
`PossibleDuplicateIsNeverForcedAndReceptionIsOffered`, `AgentWaitingTests` entero.

**Guardas del paso 3, también sin tocar:** `BookedAppointmentIsStatedWithItsDateAndOffersReminders`,
`OverlapIsOnlyEscalatedWhenAnotherActiveAppointmentHoldsTheSlot`,
`SayingYesToTheProposalJustMadeConfirmsIt`, `CancellingIsTwoTapsAndOnlyHappensOnTheSecond`,
`ChangingTheDateListsFreeHoursAndMovesThatAppointment`, `BookingIsTraceableInTheCrmAndInHospital`.

## 5. Pruebas por criterio

Todas por `AgentRuntime.Run`. Cada montaje afirma `Assert.Single(intake)` justo después del toque:
sin eso el rojo sería «The model must not be consulted», que es ruido.

| Criterio | Prueba | Rojo esperado, o mutación |
|---|---|---|
| T-3, INV-T-1 | `TappingAFreeHourWithoutARecordKeepsItAndOpensTheForm`. Mensaje: `StartsWith("Para apartar el *")`, contiene «a las 09:00», «son 7 datos cortos» y «¿Cuál es tu nombre?». Un `intake`, cero `proposal:*`, registro del Hospital vacío | `Assert.Single() Failure: The collection was empty` |
| T-4 | `AStaleOrMalformedHourOpensNothingWithoutARecord`, teoría con los dos `InlineData` de la prueba original, sin `h.Link()` | `Sub-string not found: "ya no está disponible"` |
| T-10 | `AMinorInTheFormIsOfferedReception` remontada: más cero `intake`, registro vacío, ningún `Activity.Body` con el `startsAt` | el `Assert.Single(intake)` del montaje |
| T-11, INV-T-5 | `LeavingTheFormForgetsTheChosenHour`, teoría con «salir» y «¿A qué hora abren los sábados?», con un modelo que responde. Cero `intake`; ningún `Activity.Body` con el `startsAt` | Montaje rojo; el resto nace verde. **Mutación:** escribir la hora en el cuerpo del `Trace` |
| T-11 (30 min) | `AfterThirtyMinutesTheChosenHourIsGone`: retrasar `CreatedAt` 31 minutos y responder «Ana Sintética». Cero `intake`, `model.Calls == 1` | `Assert.Empty() Failure`: la fila vencida sigue ahí |
| T-5, T-12 | `TheFinishedFormIsOneCardWithTheDataAndTheAppointment`: T-3 más siete respuestas. `*Cita:*`, `*Con:* Dra. Sintética Rivas`, `EndsWith("¿Confirmo tu registro y tu cita?")`, `ReadsLikeAChat`, un solo `proposal:*`, cero escrituras, y ningún `h.Sent` casa con `(?i)\b(apartad\|reservad\|agendad)[ao]\b`. Los tres botones se afirman en el paso 5 | `Sub-string not found: "*Cita:*"`. T-12 nace verde. **Mutación:** cambiar el lead a «Ya quedó apartado el…» |
| (plan) | `AnHourThatPassedDuringTheFormIsLeftOutOfTheCard`: editar el `slot` del `intake` a una fecha pasada antes de la séptima respuesta. Sin `*Cita:*`, `EndsWith("¿Están correctos?")` | **Nace verde.** Mutación: quitar la comprobación de futuro |
| T-6, INV-T-3 | `ConfirmingChecksTheHourRegistersLinksAndBooksInThatOrder`. La secuencia de §1; un POST de cada tipo; `visitKind == "first-visit"`; `patientId` igual al creado; «ya te registré», «quedó agendada», botón `ACTIVAR RECORDATORIOS` | `Assert.Equal() Failure` en la secuencia: falta `POST /v1/agenda` |
| T-7 | `AnHourTakenAtConfirmingStillRegistersAndOffersOthers`: la agenda devuelve la hora elegida con `takenBy = 1` y otra libre. Un POST de paciente, cero de cita, `PatientId` no nulo, lista en `h.Interactive[^1]` | `Sub-string not found: "Ese horario se acaba de ocupar"` |
| T-7 (plan) | `AnHourAlreadyPastAtConfirmingIsTreatedAsTaken`: editar el `startsAt` de la propuesta a una fecha pasada. Mismos asserts, `Status == "agent"` | el mismo; sin la guarda, `ArgumentException` |
| T-8 | `APossibleDuplicateBooksNothingAndTellsTheTeamTheHourWanted`. `forceCreateDespiteDuplicate == false`, `PatientId` nulo, cero POST de cita, y el `handoff_offer` contiene el día y «a las 09:00» | `Sub-string not found: "a las 09:00"` |
| T-9, INV-T-4 | `WhenBookingFailsAfterRegisteringAPersonTakesOverAndNothingIsRetried`: `POST /v1/agenda` responde 500. `Status == "human"`, un POST de cada tipo, `PatientId` no nulo, `patient_registered`, ningún `h.Sent` con «quedó agendada», y el `handoff` nombra la hora | `Assert.Equal() Failure`: cero POST de agenda |
| T-1 | `MenuForSomeoneWithoutRecordOffersTheFreeHours`: `Assert.Single(h.Interactive)` de tipo `list`, cero `intake`, `model.Calls == 0` | `Assert.Single() Failure: The collection was empty` |
| T-1 (bordes) | `MenuWithNothingToOfferOffersAPersonWithoutARecordToo`, teoría con agenda vacía y Hospital caído. `handoff_offer`, `Status == "agent"`, cero `intake` | `Assert.Contains() Failure`: no hay `handoff_offer` |
| T-2 | `ContactWithoutRecordGetsTappableSlotsToo`: `Assert.Single(h.Interactive)`, tipo `list`, id `StartsWith("CITA ")` | `Assert.Single() Failure: The collection was empty` |
| (plan) | `ARequestForSomeoneElseGetsNoTappableHoursWithoutARecord`: «una cita para mi papá», el modelo llama a `hospital_availability`; `Assert.Empty(h.Interactive)` | **Nace verde.** Mutación tras el paso 4: quitar la excepción `forOther` |
| T-13 a | `CorrectingTheDataRestartsTheFormAndKeepsTheHour`, `NoModel`. Ids de botón `["CONFIRMAR x", "CORREGIR x", "AGENDAR"]`; tras tocar: un `intake` cuyo `slot` es el mismo, pregunta 1, y `CONFIRMAR <viejo>` responde «ya venció o no existe» | `Assert.Equal() Failure` en los ids |
| T-13 b | `AnotherHourReissuesTheCardWithTheSameData`, `NoModel`. `AGENDAR` da la lista; el toque da una tarjeta con el mismo nombre y la hora nueva; un solo `proposal:*`; código viejo vencido; cero escrituras | el mismo de ids; después, `Sub-string not found` del nombre |
| T-15 | `ThePromptSendsANewClientToTheFreeHoursFirst`, y `ProposingForSomeoneWithoutARecordPointsToTheList` | `Assert.Contains() Failure`. Que el modelo obedezca es hueco |
| T-14 | las tres de la spec, sin tocar | regresión |

Huecos: T-15 (solo evals), INV-T-2 fuera del recorrido de T-5, y `AgentLiveJourney`.

## 6. Riesgos

- **El paso más peligroso es el 3 (`Confirm`).** Es el único método por el que pasa toda escritura
  en Hospital: crear, mover, cancelar y registrar. S3 pone dos POST sin idempotencia en un mismo
  turno y mueve la comprobación de horario antes del registro. Un error de orden crea un paciente
  sin vincular o una cita sin paciente verificado. Lo cubren T-6…T-9 más las seis guardas sin
  tocar de §4. Revisar que el límite de 2 minutos de `AgentWorker` entre los dos POST cae en el
  `catch` de `:621`, con el vínculo ya guardado.
- **Paso 4:** cambia lo que ve todo paciente nuevo y remonta nueve pruebas. Riesgo de verde en
  vacío si un remontaje deja de pasar por el formulario; cada uno conserva sus asserts íntegros.
- **Tercero sin expediente:** sin la excepción `forOther`, T-2 abre un camino para registrar a
  otra persona bajo el teléfono de quien escribe.
- **El prompt cambia el camino de todas las evals `registro-*` y de `urgencia-004`.** Sin puerta.
- **La transcripción conserva el toque.** El mensaje entrante `CITA <inicio> …` queda en
  `Messages`, como hoy para quien tiene expediente. INV-T-5 mira solo `Activities`.
- **Una tarjeta sin confirmar conserva sus datos y la hora** en `proposal:*`, legible por
  `/api/activities`. Ya ocurre hoy con los datos personales; S3 añade la hora (P-4).
- La vía del modelo no pasa datos ya dichos al formulario con hora: quien dijo su nombre y luego
  toca un horario lo vuelve a escribir.

## 7. Lo que NO se hace

- Migración, tabla, tipo nuevo o acción nueva de propuesta: un campo y cuatro claves bastan.
- Reserva provisional o cualquier escritura antes de Confirmar; comprobar el horario al tocarlo.
- Barrido por reloj de formularios vencidos (P-2).
- Servir toques de quien no tiene expediente durante la espera (`:53`).
- Conservar respuestas cuando se toca otro horario a mitad del formulario.
- Aceptar una hora escrita, sin toque, de quien no tiene expediente.
- Dar al «Corregir datos» de la tarjeta de registro sin hora el manejador nuevo (T-14 la quiere
  como está).
- `first-visit` repetido (`:606`), terceros, menores, menos preguntas, tarjeta de contacto,
  cualquier cambio en Hospital.
- Corregir que `AGENDAR` tocado a mitad del formulario se lea como nombre: es previo a S3.
- Correr `eval-agent.sh` o `live-agent.sh`; `git add`, commit o cambio en el checkout compartido.

## 8. Preguntas al propietario

Ninguna es clínica y ninguna exige migración.

- **P-1 (asumida).** Tras tocar «Otro horario», la tarjeta anterior sigue confirmable hasta que se
  toca una hora nueva. Quien mira la lista y no encuentra nada mejor puede volver a Confirmar.
- **P-2 (asumida).** «30 minutos eliminan la hora» se cumple al siguiente mensaje del paciente, no
  por reloj. Por reloj sería un barrido en `AgentWorker`.
- **P-3 (asumida).** Textos nuevos que la spec no fija: el mensaje final de T-6; el lead de
  «Corregir datos», que reutiliza el de T-3; el motivo para el equipo en T-9; el párrafo del prompt.
- **P-4 (asumida).** Una tarjeta única sin confirmar conserva datos y hora como cualquier
  propuesta de hoy. Si debe borrarse al vencer, es otro cambio, y toca datos personales.
- **P-5.** Antes de desplegar conviene correr `scripts/eval-agent.sh` y `scripts/live-agent.sh`:
  el prompt cambia el camino de todo paciente nuevo y la suite no lo ve. Usan modelo real y
  Hospital local: la decisión es del propietario.
