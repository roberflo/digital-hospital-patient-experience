# El formulario de registro habla como la gente (S1) y salta a lo que falta (S2)

Petición del propietario (2026-10-04): «Repasa la experiencia del chat para que sea más natural;
inicia por el tema de sexo registral y en especial crear el cliente antes de la cita.»
Este documento cubre el texto del formulario (S1) y su recorrido (S2). «La hora antes que el
registro» es `agent-slot-first.md`. Amplía `reception-agent.md` (criterios 34, 39, 43) y enmienda
`agent-evidence.md` donde se indica. Anclaje: `main` local, 9 commits delante de `origin/main`.

## Lo que ya existe

- Formulario de siete preguntas sin modelo (`AgentIntake.cs`); sexo con dos botones (`:59`),
  justo después de la fecha; el menor se detecta antes de preguntar el sexo (`:41`).
- «Registral» llega al paciente por `AgentIntake.cs:59` y por el prompt (`AgentRuntime.cs:145`).
  La tarjeta dice «Sexo: femenino» (`:463`) y no cambia.
- `Intake.Start(today, known)` toma lo ya dicho en orden y corta en el primer hueco (`:23,25`).
- `AgentRuntime.cs:46` reconoce el registro buscando «contacto de emergencia» en el mensaje
  anterior del agente.

## Lenguaje

**Dato**: uno de los siete del registro. **Falta**: no se tiene un valor que encaje.
**Sexo del documento**: el que consta en el documento de identidad; es lo que hoy se pregunta
como «sexo registral» y se envía a Hospital como `sex` (`female`/`male`).

## Invariantes

- INV-V-1: el valor de `sex` que llega a Hospital, y las respuestas que lo producen, no cambian.
- INV-V-2: ningún mensaje del servidor al paciente contiene «registral».
- INV-V-3: las preguntas 5, 6 y 7 contienen «contacto de emergencia».
- INV-H-1: el formulario nunca pregunta un dato que ya tiene con un valor que encaja.
- INV-H-2: un valor que no encaja no se guarda.
- INV-H-3: una fecha de nacimiento de menor termina el formulario en cuanto se conoce.
- INV-H-4: mientras no haya una fecha de nacimiento aceptada (de adulto), no se toma ni se guarda
  ningún dato posterior a ella (sexo, contacto de emergencia). Añadido tras la revisión adversaria
  del 2026-10-04: sin esto, el salto guardaba datos de un posible menor antes de saber su edad.

## Criterios — S1 (texto)

Las pruebas de `Intake` son puras; las de recorrido usan `AgentRuntime.Run` con `AgentHarness`
y `NoModel`. Se corren con `scripts/test-backend.sh "<filtro>"`.

| N.º | Criterio | Cómo se prueba |
|---|---|---|
| V-1 | La pregunta del sexo es «¿Qué sexo aparece en tu documento de identidad?\nEs un dato que pide el hospital para registrarte.», con los botones Femenino / Masculino (ids `f`, `m`). | `AgentIntakeFlowTests.SexIsAnsweredWithButtons` ampliada: `prompt` contiene «documento de identidad» y no «registral». Ejercita `Intake.Answer` → `Ask` (`AgentIntake.cs:59`). Rojo hoy: contiene «registral». |
| V-2 | Lo aceptado como respuesta y lo enviado a Hospital no cambian (INV-V-1). | Sin modificar y en verde: `AnAnswerThatDoesNotFitIsAskedAgainNeverGuessed` (caso `4, "no sé"`), `SevenAnswersCompleteARegistration`, `AgentIntakeTests.NewClientIsRegisteredOnlyAfterConfirmingAndCanThenBook` (`sex == "female"` en el POST). |
| V-3 | Ningún mensaje del recorrido completo contiene «registral» ni «Paso » (INV-V-2). | `AgentVoiceTests.EveryStepOfRegisteringReadsLikeAChat` ampliada: `Assert.All(h.Sent, …DoesNotContain)` para ambas cadenas. Recorre AGENDAR → 7 respuestas por `AgentRuntime.Run`. Rojo hoy. |
| V-4 | Primer mensaje sin datos previos: «Con gusto. Para darte cita necesito registrarte: son 7 datos cortos.\n\n¿Cuál es tu nombre?\nSolo nombres; los apellidos te los pido enseguida.» | `NewClientTapsAgendarRegistersStepByStepAndSeesTheFreeHoursWithoutTheModel`: el assert «Paso 1 de 7» (`:88`) pasa a «son 7 datos cortos» y «¿Cuál es tu nombre?». Igual en `AgentInteractionTests.MenuForSomeoneWithoutRecordStartsTheRegistrationForm` (`:232`) y `AgentVoiceTests.AgentStartsTheGuidedFormInsteadOfListingEverythingItNeeds` (`:121`). |
| V-5 | Preguntas 2, 3, 5, 6 y 7: el texto actual sin la línea «*Paso N de 7*». | `EveryStepOfRegisteringReadsLikeAChat`: `h.Sent` contiene, en orden, «¿Y tus apellidos?», «fecha de nacimiento», «¿Quién es tu contacto de emergencia?», «parentesco tiene contigo», «teléfono de tu contacto de emergencia». |
| V-6 | Progreso ligero: el primer mensaje dice cuántos datos faltan (V-4, o V-7 si ya hay datos) y la última pregunta empieza con «Último dato:\n». Ninguna otra lleva contador. | Prueba pura nueva sobre `Intake`: en el recorrido de siete, solo el `prompt` de la séptima empieza por «Último dato:». |
| V-7 | Con datos ya dichos: «Ya tengo parte de tus datos; me faltan N.\n\n» + la pregunta; con N = 1, «me falta uno.» | `WhatThePatientAlreadySaidIsNotAskedAgain`: el assert «Paso 4 de 7» (`:147`) pasa a «me faltan 4» más la pregunta del sexo. `AModelThatSaysNothingAfterStartingTheFormStillStartsIt` (`:206`): «Paso 2 de 7» pasa a «¿Y tus apellidos?». |
| V-8 | Tras confirmar el registro: «Listo, Ana, ya te registré.\n» + los horarios como hoy. | Mismo test de V-4: el assert «ya tienes tu expediente» (`:97`) pasa a «ya te registré». Ejercita `Register` (`AgentRuntime.cs:634`). |
| V-9 | Todo mensaje del formulario cumple el criterio 39 (≤130 caracteres por línea). | `AgentVoiceTests.EveryStepOfRegisteringReadsLikeAChat`, sin debilitar. |
| V-10 | «Emergencia: Carlos Sintético» como respuesta a la pregunta 5 no deriva (INV-V-3). | Prueba nueva por `AgentRuntime.Run`: AGENDAR, cuatro respuestas, y ese texto. Assert: `Status == "agent"` y ninguna actividad `handoff`. Nace verde: su rojo se demuestra quitando la frase de la pregunta 5 y pegando el fallo. `AgentGuardTests.EmergencyContactIsRegistrationDataNotAnEmergency` no basta: prueba la función, no el acoplamiento con `:46`. |
| V-11 | El prompt del modelo no dice «registral»: pide «el sexo que aparece en su documento de identidad (femenino o masculino)». | Prueba nueva con el patrón de `AgentMemoryTests.TheStablePartOfThePromptComesFirst` (`Said(seen[0], "system")`): el mensaje de sistema capturado no contiene «registral». **Hueco declarado:** que el modelo use esas palabras solo lo ven las evals. |
| V-12 | Los casos de eval que exigen «Paso \d de 7» (`urgencia-004-cliente-nuevo-de-noche`, `registro-003-datos-incompletos-no-se-suponen`) pasan a exigir «me faltan?» o «datos cortos». | `AgentEvalCasesAreValid` solo valida la forma. **Hueco declarado:** `scripts/eval-agent.sh` usa modelo real y comparte clave con el agente en vivo; no es puerta. |

## Criterios — S2 (saltar a lo que falta)

| N.º | Criterio | Cómo se prueba |
|---|---|---|
| H-1 | `Intake.Start(today, known)` toma todo valor conocido que encaja, esté donde esté, y pregunta el primer dato que falta en el orden del formulario. | Pura: `Intake.Start(Today, "Rosa Sintética", "Prueba", "8 de enero de 1985", null, "Carlos Sintético", "hermano", "7000 0001")` devuelve la pregunta del sexo con botones, y el estado conserva los tres datos del contacto. Rojo hoy: `EmergencyName` es nulo. |
| H-2 | Tras una respuesta aceptada, la siguiente pregunta es el siguiente dato que falta; sin faltantes, el formulario está completo. | Sobre el estado de H-1: `.Answer("Femenino", Today)` da `Complete`. Rojo hoy: pregunta el nombre del contacto. |
| H-3 | Un valor conocido que no encaja se pregunta y no se guarda; los que encajan después de él sí se conservan, **salvo cuando lo que falta o no encaja es la fecha de nacimiento: entonces nada posterior se toma (INV-H-4)**. | `AKnownAnswerThatDoesNotFitIsAskedNotKept` queda como estaba (fecha ilegible → `Assert.Null(state.Sex)`), más `BirthDate == null`. Nueva pura: nombres, apellidos, «tengo 16 años», sexo y contacto → `Step == 3`, sexo y contacto nulos, «me faltan 5». Nueva por `AgentRuntime.Run`: ningún `Activity.Body` contiene el nombre ni el teléfono del contacto. Nueva `Theory`: con todo válido salvo un campo que no es la fecha, ese campo queda nulo y el resto se conserva (INV-H-2). |
| H-4 | Una fecha conocida de menor termina el formulario aunque falten datos anteriores (INV-H-3). | Pura nueva: `Intake.Start(Today, null, null, "01/01/2010").State.Minor`. Sin modificar y en verde: `AMinorIsNoticedAsSoonAsTheBirthDateIsKnown`, `AMinorIsNotRegisteredByChat`, `AMinorInTheFormIsOfferedReception`. |
| H-5 | Por la vía del modelo, si solo falta el sexo, el paciente recibe una pregunta y, tras tocar, la tarjeta. | Nueva por `AgentRuntime.Run`: el modelo emite `start_registration` con seis datos, sin `sex`. Assert: `h.Sent[^1]` contiene «documento de identidad» y `h.Interactive[^1]` tiene dos botones. Luego `Say("Femenino")` con `NoModel`: la tarjeta contiene «Contacto de emergencia: Carlos Sintético». Ejercita `AgentRuntime.cs:230` e `IntakeStep` (`:470`). |
| H-6 | El recorrido sin modelo desde cero y la inferencia «mi hija Sofía» no cambian. | Sin modificar y en verde: `SevenAnswersCompleteARegistration`, `ARelationshipSaidInFrontOfTheContactsNameIsNotAskedAgain`, `AgentTraceTests.TheFormThanksByName`, `RegistrationFormInProgressNeverShowsInTheHistory`. |

## Enmiendas a otras specs (dichas, no silenciosas)

- `agent-evidence.md` EV-2 y EV-8 afirman «Paso 1 de 7» / «Paso 4 de 7»: pasan a afirmar la
  pregunta («¿Cuál es tu nombre?», «documento de identidad»).
- `agent-evidence.md` EV-10 («el formulario sin modelo no cambia»): esta spec lo cambia en texto
  (S1) y en recorrido con datos previos (S2). EV-10 pasa a referirse a lo que queda tras esta spec.
- A-2 de `agent-evidence.md`: con S2 preguntar siempre el sexo con botones cuesta un toque
  (H-5). **S2 va antes que EV-8.** Aplica igual a cualquier dato retenido por EV-2…EV-6.
- `reception-agent.md` criterio 34: «siete preguntas» sigue siendo cierto; «sexo registral» pasa
  a «sexo del documento».

## Anti-criterios

- Cambiar qué se captura: tercera opción, «prefiero no decir», género, omitir el dato, o
  inferirlo del nombre o del parentesco.
- Dar una razón clínica para el dato («para tus exámenes», «para el tratamiento»): el agente no
  explica clínica.
- Quitar «contacto de emergencia» de las preguntas 5–7, o cambiar «parentesco tiene» sin
  actualizar `ARelationshipSaid…`, cuyo `DoesNotContain` pasaría en verde y en vacío.
- Borrar un assert de «Paso N de 7» en vez de sustituirlo por el del texto nuevo.
- Unir nombres y apellidos en una pregunta y partirlos por código: es suponer.
- Reordenar las preguntas o pedir el sexo antes que la fecha.
- Que S2 guarde un valor que no encaja «para después».
- Rellenar un dato desde `contact.Name`, memoria o `recall`.
- Resolver solo en el prompt lo que el formulario puede decir.
- Tocar la tarjeta (`AgentRuntime.cs:463`), o `:457`, que no llega al paciente.
- Correr evals con la clave de NIM mientras el agente atiende.

## Fuera de alcance (por nombre)

Hora antes que el registro (`agent-slot-first.md`) · diferir o reducir el contacto de emergencia
(B-3) · tarjeta de contacto de WhatsApp (`contacts` no se lee: `WhatsAppContent.cs:8-17`) · botón
«Corregir datos» sin manejador propio · la palabra «expediente» en `AgentRuntime.cs:76, 107, 495,
625, 628` · los textos de historial de 10 casos de eval que citan «sexo registral» · los cuatro
nombres de prueba inexistentes de `reception-agent.md` (criterios 18, 32, 35) · `first-visit`
repetido: `AgentRuntime.cs:606` marca `first-visit` toda cita de un contacto registrado por chat,
no solo la primera · cualquier cambio en Hospital.

## Preguntas

**Bloqueante**

- B-1 — Qué sexo necesita el médico. Recepción pregunta el del documento; `Sex.cs:4` de Hospital
  lo define como «biological sex as recorded at registration». La contesta el propietario con el
  médico del piloto. **No bloquea S1 ni S2**: S1 conserva lo que hoy se pregunta. Bloquea
  cualquier cambio de significado u opciones. **Hasta entonces se queda como está**: sexo del
  documento, dos valores.

**Asumidas**

- A-1: «registral» significa «el que aparece en el documento de identidad»; el texto nuevo dice
  lo mismo con otras palabras.
- A-2: la razón que se da es administrativa («lo pide el hospital para registrarte»).
- A-3: el progreso se muestra al inicio (cuántos faltan) y al final («Último dato»), sin contador
  en cada mensaje.
- A-4: «expediente» sale solo de los dos mensajes del registro (entrada y cierre); donde nombra
  el expediente médico real se queda.
- A-5: se pregunta «documento de identidad» y no «DUI»: el nombre del documento depende del país.
- A-6: un formulario a medias en el momento del despliegue puede repetir una pregunta; vence en
  30 minutos.
- A-7: los nombres de prueba nuevos son propuestos; hoy no existen.
- A-8: cuando los apellidos ya se conocen y falta el nombre, la segunda línea de la pregunta 1 es
  «Solo nombres, sin apellidos.» en vez de «…los apellidos te los pido enseguida.», que sería
  falso. Añadido tras la revisión adversaria del 2026-10-04.

## Hallazgos de la revisión que quedan abiertos

- **B-1 pesa más de lo que A-1 supone.** El texto nuevo afirma en nombre del hospital que pide «el
  sexo del documento», y Hospital lo guarda como sexo biológico clínicamente determinante. El dato
  no empeora respecto a «registral», pero la afirmación es nueva. Sigue siendo del propietario con
  el médico del piloto.
- La pregunta del sexo no tiene salida: «prefiero no decirlo» recibe los dos botones una y otra
  vez, sin oferta de persona (preexistente; eval `registro-104` sin `match`). Depende de B-1.
- Terceros: tras un «registra a mi mamá» bloqueado, los datos de ella en el mensaje siguiente se
  aceptan (`forOther` solo mira el último mensaje); solo la tarjeta lo frena. Preexistente; lo
  resuelve `agent-evidence.md`.
- Un formulario abandonado no se borra: los 30 minutos son un filtro de consulta. Preexistente.
- Sin correr: evals, `AgentLiveJourney.cs`, y el efecto del cambio de prompt sobre el modelo.
