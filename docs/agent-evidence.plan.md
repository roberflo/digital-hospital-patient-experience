# Plan — Evidencia, no confianza (S1)

> **Cerrado (2026-10-23)** — `2bbb8e5` (montajes), `21d1939` (EV-1…7, 9, 10), `a795e5b` (EV-8). EV-1…EV-10 hechos, **incluido EV-8**. Suite 1356 → 1368
> (+12). Ejecución y salidas literales al final («Ejecución 2026-10-23»).
>
> Re-anclado el 2026-10-23 contra `main` `ff5a80c`, después de intake-voice (`30081e5`) y
> slot-first (`37bb7ed`). **EV-8 decidido por el propietario el 2026-10-23: «sí a las dos».** Se
> pregunta siempre el sexo del documento con los dos botones; desaparece P-1.

## Pregunta para el propietario (junta A-2 de esta spec y B-1 de agent-intake-voice) — contestada: «sí a las dos» (2026-10-23)

> **Cuando un paciente nuevo se registra por WhatsApp, ¿le pedimos siempre con los dos botones
> (Femenino / Masculino) el sexo que aparece en su documento de identidad, aunque ya lo haya
> escrito en el chat, y es ese el sexo que necesita el médico?**

En palabras simples: hoy, si el paciente escribe «mujer» u «hombre», el agente lo toma como su
sexo, pero «mi mujer Ana» habla de otra persona. Preguntarlo siempre con botones le cuesta al
paciente un toque más y evita ese error. Además, Recepción pide «el sexo de tu documento» y
Hospital lo guarda como sexo biológico para uso clínico (`Sex.cs:4` de Hospital). Por eso conviene
confirmarlo con el médico del piloto. Respuestas posibles:

- **«Sí a las dos»:** EV-8 se desbloquea tal como está escrito abajo (§6) y desaparece P-1.
- **«Botones sí, pero el médico necesita otro dato»:** EV-8 sigue bloqueado. Antes hay que cambiar
  la pregunta y sus opciones (B-1), y eso es una spec aparte.
- **«No hace falta preguntarlo siempre»:** EV-8 se descarta y se queda P-1, que toma el sexo
  escrito con el vocabulario del paso 4.

Spec: [agent-evidence.md](agent-evidence.md). Primera medición el 2026-10-04 contra `139f4e2`. La
re-medición de hoy fue solo lectura y no corrió pruebas, porque Docker está apagado (§0).

## 0. Estado del árbol (2026-10-23)

- `main` = `ff5a80c`. Árbol limpio salvo `propuestas/` sin seguimiento, que este plan no toca.
- Lo que el plan suponía y ya no existe: no hay `Intake.Wrote` ni `SexOf`. `propose_registration`
  sigue llamando a `ProposeRegistration` directamente (`AgentRuntime.cs:634`), y
  `start_registration` toma los valores del modelo sin filtro (`:638`).
- Recepción no tiene `AGENTS.md` ni `CLAUDE.md`; el comando de pruebas está en `README.md:65`.

### Qué cubrieron ya intake-voice y slot-first

| Pieza del plan original | Estado ahora | Efecto en este plan |
|---|---|---|
| «Un regex sin anclas que localiza fechas en texto libre» | **Hecho a medias** por intake-voice: `Intake.Date` (`AgentIntake.cs:93-102`) ya lee la fecha en cualquier parte de la frase, pero devuelve **solo la primera**. | Solo falta enumerar **todas** las fechas de `said` (EV-6) con los cuatro regex que ya existen (`:108-111`). No hace falta un regex nuevo. |
| Riesgo «lo que sigue a un dato retenido también se pregunta» | **Resuelto** por intake-voice S2: `Intake.Start(today, known)` (`:25-38`) salta al dato que falta. La excepción es la fecha: sin fecha aceptada no se lee nada posterior (`:30`, INV-H-4). | El riesgo vale solo para la fecha. Cuando el filtro retiene un nombre o un teléfono, se pregunta solo ese dato. |
| §6: EV-8 «cuesta un toque pero vuelve a preguntar el contacto» | **Resuelto** por S2: si falta el sexo se pregunta solo el sexo. Lo fija `WhenOnlyTheSexIsMissingThePatientTapsOnceAndGetsTheCard` (`AgentIntakeFlowTests.cs:316`). | EV-8 es solo un filtro y no requiere cambiar el formulario. |
| Asserts «Paso N de 7» | **Ya no existen.** intake-voice cambió los textos (`AgentIntake.cs:81-87`). | Todos los asserts de §4 afirman la pregunta (tabla en §4). |
| EV-9 para un menor | **Parcial**: H-3/INV-H-4 ya impide guardar el sexo y el contacto de un menor posible (`WithoutAReadableBirthDateTheStoredFormHoldsNothingAfterIt`, `AgentIntakeFlowTests.cs:252`). | EV-9 se ocupa solo de los valores que no escribió el paciente. |
| EV-2/EV-7 en la vía «tocar hora» | **Cubierto** por slot-first: tocar un horario abre el formulario sin datos del modelo (`AgentRuntime.cs:501`, `:527`). El prompt pide no pedir datos antes de la hora (`:188-189`). | Las dos lambdas del modelo siguen abiertas: ahí está todo el trabajo. |
| «Otro horario» en la tarjeta | Nuevo con slot-first: vuelve a proponer con los datos de la tarjeta (`:494-499`), que el paciente ya revisó. | Queda **fuera** del filtro (§7). |

### EV-1: el harness permite ver `tools` sin cambiarlo

- `AgentHarness` está en `AgentGuardTests.cs:981`. `h.Model(...)` descarta la petición (`:1051`).
- `AgentHarness.Fake` (`:1100`) recibe el `HttpRequestMessage` completo. `AgentMemoryTests.cs:16`
  ya clona el cuerpo entero, y `Said` (`:18`) solo lee `messages`.
- La prueba nueva usa un `Fake` propio de tres líneas que guarda el cuerpo y lee `tools`.
- **Cambio frente al plan original:** `Tools()` (`AgentRuntime.cs:590-705`) ofrece ahora **17**
  herramientas, no 13. Además, `propose_action` declara `kind`, el tipo de visita de slot-first y
  VisitKind. Con `kind` en el regex prohibido, EV-1 nacería rojo por la razón equivocada. El regex
  queda así: `(?i)confiden|confianza|score|puntaje|certeza|url|source|fuente|evidenc`.
- **Sin verificar:** que el cuerpo traiga `tools[].function.parameters.properties`. Si no lo trae,
  `GetProperty("tools")` lanza y la prueba sale roja, no vacía.

### Cómo correr las pruebas

- `scripts/test-backend.sh "<filtro>"` con cwd en la raíz del árbol (`README.md:65`). Usa `.env`,
  `$PWD:/src` y `docker compose exec db` relativos.
- Necesita Docker, el servicio `db` levantado, la red `recepcion_default` y `.env` con
  `POSTGRES_PASSWORD`. Sin `TEST_DATABASE` el harness lanza (`AgentGuardTests.cs:993`): sale rojo,
  no se salta.
- Sin argumento, el filtro es `Category!=Eval&Category!=Live`. **Un filtro propio reemplaza al de
  defecto.** `FullyQualifiedName~Agent` incluye `AgentEvals`, que llama al modelo real
  (anti-criterio). Usar solo nombres de clase: `FullyQualifiedName~AgentEvidenceTests`,
  `FullyQualifiedName~AgentIntake`, o la suite completa sin argumento.
- **Línea base sin medir hoy**: Docker está apagado. El último conteo registrado es
  `1332/1332`, en `d4dce0c` (2026-10-20). Desde entonces han entrado commits con pruebas, así que
  el paso 0 lo vuelve a medir.

## 1. Archivos que cambian

| Archivo | Cambio |
|---|---|
| `backend/AgentIntake.cs` | `Intake.Wrote(...)`; un enumerador privado de todas las fechas de un texto, con los regex de `:108-111` y la misma validación de `Date` (`:101`); el mapa de sexo del paso 4 (`:68`) sale a `SexOf` y `case 4` lo usa |
| `backend/AgentRuntime.cs` | solo las dos lambdas de registro: `:634` (`propose_registration`) y `:635-640` (`start_registration`) |
| `backend/AgentMemory.cs` | `Fold` (`:60`) pasa de privado a `internal` |
| `backend.Tests/AgentEvidenceTests.cs` | nuevo: EV-1, 2, 4, 5, 6, 7, 9 |
| `backend.Tests/AgentIntakeFlowTests.cs` | montaje de 6 pruebas |
| `backend.Tests/AgentIntakeTests.cs` | montaje del helper y de 3 llamadas |
| `backend.Tests/AgentVoiceTests.cs` | montaje de 1 `Theory` (2 casos) |
| `backend.Tests/HospitalIdempotencyTests.cs` | montaje del helper `Propose` (`:47`) cuando `tool == "propose_registration"` |

No se tocan: `AgentHarness`, `AgentGuard.cs`, el prompt (`AgentRuntime.cs:153-216`),
`ProposeRegistration` (`:877-899`), `IntakeStep` (`:902-913`), el bloque `startIntake`
(`:280-298`), `Correct` (`:519-530`), «Otro horario» (`:494-499`), `Intake.Start`/`Answer`/`Ask`,
las migraciones ni las evals.

### La única función nueva

```csharp
// AgentIntake.cs — field: 0..6 en el orden del formulario
public static bool Wrote(int field, string value, IReadOnlyCollection<string> said, DateOnly today)
```

| Campo | Regla |
|---|---|
| 0, 1, 4, 5 (nombres, apellidos, nombre y parentesco del contacto) | cada palabra de `AgentMemory.Fold(value)` está en el conjunto de palabras de `Fold` de los mensajes del paciente |
| 2 (fecha) | `Date(value)` existe, y las fechas válidas distintas halladas en `said` son exactamente esa. Válida significa `<= today` y `>= today-120`, como el paso 3 (`:64`). Si hay más de una distinta, se retiene (EV-6) |
| 3 (sexo) | `SexOf(value)` existe, y los `SexOf(palabra)` no nulos del paciente son exactamente ese valor |
| 6 (teléfono) | `Rules.Phone(value)` es igual a `Rules.Phone` de alguna corrida con forma de teléfono en `said`. Un `ArgumentException` cuenta como falso |

En `AgentRuntime` las dos lambdas hacen lo mismo:

- `said` = cuerpos de `session` con `Sender == "patient"` (A-3), la misma lista que ya usa la
  inferencia del parentesco (`:287`).
- `startIntake = true; known =` los siete valores; cada uno es nulo si está en blanco o si
  `!Intake.Wrote(...)`.
- `propose_registration` deja de llamar a `ProposeRegistration` directamente. Devuelve el mismo
  `Result(new { started = true, … })` que `start_registration` y entra por el bloque `:280-298`.
- Si constan los siete, `Intake.Start` completa y sale la tarjeta de siempre (`:290`). Si no, el
  formulario pregunta solo lo que falta (S2) y se descarta el texto del modelo.
- La inferencia «mi hija Sofía» (`:286-287`) ya lee texto del paciente y ve `known[4]` filtrado:
  no cambia.
- El rechazo «para otra persona» (`:572`) corre antes de la lambda y no cambia.

## 2. Orden del trabajo

**Paso 0 — línea base.** Crear un worktree privado desde `main`:
`git -C <Recepcion> worktree add <scratchpad>/wt-evidence main`. Enlazar `.env` dentro del
worktree; como `docker-compose.yml` fija `name: recepcion`, el script funciona desde otro
directorio. Correr la suite sin filtro. El conteo es la puerta de este plan.

**Paso 1 — montajes (árbol verde, mismo conteo).** Sembrar los mensajes de la sección 3. Hoy
todo se toma, así que siguen verdes. Esto demuestra, antes de tocar producción, que los textos
sembrados pasan `AgentGuard.Inbound` y `ForSomeoneElse` (`AgentGuard.cs:98`, regex `:219`).

**Paso 2 — rojos.** Escribir `AgentEvidenceTests.cs`, correr y pegar cada mensaje de fallo.
EV-1, EV-4(a) y EV-4(d) nacen verdes.

**Paso 3 — verde.** Implementar `Intake.Wrote`, `SexOf`, el enumerador de fechas, `Fold` a
`internal` y las dos lambdas. Correr `~AgentEvidenceTests`, luego `~AgentIntake`, luego la suite
completa. El conteo debe ser la línea base más las pruebas nuevas. Para EV-1, añadir
temporalmente `string? confidence = null` a `start_registration`, pegar el rojo y retirarlo.

**Paso 4 — EV-8: decidido («sí a las dos», 2026-10-23) y hecho** en su propio commit (§6).

## 3. Montajes que cambian (los asserts no)

Los textos evitan «emergencia» fuera de «contacto de emergencia». También evitan
registr-/agend-/cita seguido de «a|para|de mi <pariente>» (`AgentGuard.cs:219`).

| Prueba | Mensaje del paciente a sembrar antes de `Run` |
|---|---|
| `AgentIntakeFlowTests.AgentHandsTheFormWhatItAlreadyKnows` (`:332`) | «Soy Irene Sintética Mora Prueba, mujer, nací el 9 de septiembre de 1979. Mi hija Sofía Sintética, 7000-0007.» |
| `…AMinorIsNoticedAsSoonAsTheBirthDateIsKnown` (`:345`) | «Me llamo Diego Sintético Prueba y nací el 01/01/2010.» |
| `…ARelationshipSaidInFrontOfTheContactsNameIsNotAskedAgain` (`:357`) | antes del mensaje que ya tiene (`:360`): «Soy Irene Sintética Mora Prueba, femenino, nací el 9 de septiembre de 1979.» |
| `…AModelThatSaysNothingAfterStartingTheFormStillStartsIt` (`:370`) | «Me llamo Rosa Sintética.» |
| `…WithoutAReadableBirthDateTheStoredFormHoldsNothingAfterIt` (`:252`) **nuevo** | «Me llamo Diego Sintético Prueba, tengo 16 años.» Sin él, el filtro retiene el nombre y la pregunta sería «¿Cuál es tu nombre?», no la de la fecha |
| `…WhenOnlyTheSexIsMissingThePatientTapsOnceAndGetsTheCard` (`:316`) **nuevo** | «Soy Rosa Sintética Prueba, nací el 8 de enero de 1985. Mi hermano Carlos Sintético, 7000 0001.» (sin sexo, a propósito) |
| `AgentVoiceTests.AFormThatStartsWithKnownDataReadsLikeAChat` (`:149`) **nuevo** | `false`: «Me llamo Rosa Sintética.»; `true`: el mismo mensaje de Rosa de la fila anterior |
| `AgentIntakeTests.ProposeAndGetCode` (`:34-38`) | primera línea del helper: «Soy Ana Sintética López Prueba, mujer, nací el 12/03/1990. Mi hermano Carlos Sintético, 70000001.» |
| `…FirstAppointmentOfARegisteredClientIsAFirstVisit` (`:85`) | el mismo, antes de `:85` |
| `…InvalidRegistrationIsNeverProposed` (`:121`) | `$"Soy Ana Sintética López Prueba, mujer, nací el {birthDate}. Mi hermano Carlos Sintético, 70000001."` |
| `…LinkedPatientIsNeverRegisteredAgain` (`:132`) | el mismo de Ana, antes de `:132` |
| `HospitalIdempotencyTests.Propose` (`:47`) **nuevo** | el mismo de Ana, solo si `tool == "propose_registration"`. Cubre los llamadores `:90`, `:102` y `:120` |

- No necesitan montaje las pruebas que llaman `start_registration` con `new { }`
  (`AgentIntakeFlowTests.cs:134`, `AgentVoiceTests.cs:118,133`, `AgentTraceTests.cs:85`), ni la de
  otra persona (`AgentIntakeFlowTests.cs:392`), que se rechaza en `:572` antes de la lambda.
- El helper `ProposeAndGetCode` cubre dos pruebas más sin tocar su cuerpo:
  `NewClientIsRegisteredOnlyAfterConfirmingAndCanThenBook` y
  `PossibleDuplicateIsNeverForcedAndReceptionIsOffered`.
- Tensión de la spec: EV-10 pide `PossibleDuplicate…` «sin modificar», y la lista de montajes
  incluye «todo lo que pasa por el helper». Sembrar dentro del helper satisface las dos lecturas.
- `:121` y `:132` pasarían sin siembra, pero en vacío: se rechazaría por falta de evidencia, no por
  la invalidez ni por el vínculo.
- **Ojo con `InvalidRegistrationIsNeverProposed`.** Con la ruta nueva, el caso «2015-06-01»
  (menor) sale por `filled.Minor` → `OfferPerson`, y no como error devuelto al modelo. El assert
  (cero propuestas, sin «CONFIRMAR») se mantiene.
- `AgentLiveJourney.cs` y `AgentEvals.cs` usan el modelo real con mensajes reales del paciente y no
  necesitan montaje. No se corren (anti-criterio).

## 4. Pruebas por criterio

Todas van en `AgentEvidenceTests`, con `AgentRuntime.Run` real. `E` son los siete datos de Irene;
`S` es el mensaje de Irene de la sección 3.

Textos de pregunta que reemplazan a «Paso N de 7» (`AgentIntake.cs:81-87`): 1 «¿Cuál es tu
nombre?», 2 «¿Y tus apellidos?», 3 «¿Cuál es tu fecha de nacimiento?», 4 «documento de
identidad», 5 «¿Quién es tu contacto de emergencia?», 6 «parentesco tiene contigo», 7 «Último
dato».

| Criterio | Prueba | Rojo esperado hoy |
|---|---|---|
| EV-1 / INV-EV-4 | `Fake` que guarda la petición. `tools` tiene 17 entradas, incluye `start_registration` y `propose_registration`, y ninguna clave de `function.parameters.properties` casa con el regex de §0 | nace verde; con `confidence` temporal falla nombrando `confidence` |
| EV-2 / INV-EV-1 | `start_registration(E)` con solo «Consulta sintética». `h.Sent[^1]` contiene «¿Cuál es tu nombre?»; cero `proposal:*` | `Assert.Contains` falla con «Revisa tus datos:…» como texto real |
| EV-2 (sexo) | `S` sin «mujer»; el modelo pasa `female`. `h.Sent[^1]` contiene «documento de identidad» | sale la tarjeta |
| EV-3 | `AgentHandsTheFormWhatItAlreadyKnows` con `S`: una tarjeta, una `proposal:*` | nace verde. Discrimina en pareja con EV-2: misma llamada, solo cambia la siembra |
| EV-4(a)(d) | `S`; el modelo pasa «09/09/1979» y «70000007»: sale la tarjeta | nace verde; sería rojo con una comparación ingenua de cadenas |
| EV-4(b) | el paciente escribe «…nací el 03/04/1990…» y el modelo pasa «1990-03-04»: «¿Cuál es tu fecha de nacimiento?», cero propuestas | sale la tarjeta |
| EV-4(c) | el paciente escribe «Soy Irene Sintética Marchetta…» y el modelo pasa `familyNames` «Marchetti»: «¿Y tus apellidos?», cero propuestas. Con S2 el resto se conserva: «me falta uno» | sale la tarjeta |
| EV-5(a) / INV-EV-2 | «Soy Irene Sintética Mora Prueba.»; el modelo pasa `E`: «¿Cuál es tu fecha de nacimiento?», cero propuestas | sale la tarjeta (S2 toma los siete) |
| EV-5(b) / INV-EV-2 | «Soy Diego Sintético Prueba, nací el 01/01/2010.»; el modelo pasa «1990-01-01» y el resto: «¿Cuál es tu fecha de nacimiento?», sin `handoff_offer`. Luego `h.Say("patient","01/01/2010")` y `Run` con `NoModel`: hay `handoff_offer` | sale la tarjeta o la pregunta del sexo: un menor avanza como adulto |
| EV-6 / INV-EV-3 | «Soy Irene Sintética Mora Prueba», «nací el 12/03/1990», «perdón, 12/03/1991»; el modelo pasa «1991-03-12»: «¿Cuál es tu fecha de nacimiento?», cero propuestas | se toma 1991 y sigue el formulario |
| EV-7 | `propose_registration(E)` con solo «Consulta sintética». Cero `proposal:*`; ningún `h.Sent` con «Revisa tus datos»; lista de `POST /v1/patients` vacía tras un segundo turno «sí» | `Assert.Empty` falla con una `proposal:XXXXXX` |
| EV-9 / INV-EV-5 | dentro de EV-2 y EV-5(a): ningún `Activity.Body` ni `h.Sent` contiene «Irene», «1979» ni «70000007» (según el caso), con el patrón de `AgentIntakeFlowTests.cs:260` | el cuerpo de `proposal:*` contiene el valor |
| EV-10 | sin tocar y verdes: todas las de `AgentIntakeFlowTests` y `AgentSlotFirstTests` no listadas en la sección 3, y el cuerpo de `PossibleDuplicate…` | — |

El segundo turno «sí» de EV-7 existe porque, sin él, «cero POST» se cumple siempre.

**Huecos declarados**

- EV-11: no tiene puerta. Desde aquí no se puede medir si la retención empeora `registro`.
- INV-EV-3: solo tiene criterio para la fecha (EV-6). Para nombres y teléfono, «dos valores
  distintos» no se puede decidir sin saber a qué campo responden las palabras.
- EV-8: decidido y hecho (§6).

## 5. Riesgos

- **El paso más peligroso es el 3, en la lambda de `propose_registration` (`:634`).** Es el
  camino que termina creando expedientes, y cambia de ruta. Cambios visibles:
  - un menor recibe la oferta de recepción en vez del texto del modelo;
  - un dato inválido se pregunta en vez de devolver error al modelo;
  - **nuevo:** con `attention.Unregistered == "person"` (`:280`), `propose_registration` ofrece
    persona en lugar de dejar una tarjeta. Hoy esa vía sí deja tarjeta aunque el hospital registre
    en recepción. Es más correcto, pero cambia algo; lo cubren los asserts de `AgentIntakeTests` y
    `HospitalIdempotencyTests`.
- **Retención de más con el modelo real.** Si el modelo corrige «Gonzales» a «González», o el
  paciente escribe «9/9/79», se pregunta de nuevo. No hay bucle: la respuesta la toma `IntakeStep`,
  sin modelo. Con S2 cuesta una pregunta, salvo que el dato sea la fecha (`AgentIntake.cs:30`).
  No está medido (EV-11).
- **Trampa del regex de teléfono.** «…1990. 7000 0001» puede leerse como una sola corrida de
  dígitos. El regex no debe cruzar puntuación seguida de espacio. EV-4(d) y los montajes ponen el
  teléfono tras una coma.
- **Trampa de fechas.** `Date` toma la primera coincidencia de cuatro regex en orden. El
  enumerador debe recorrer los cuatro con `Matches`, no repetir `Date`, o EV-6 queda verde en
  vacío.
- **«mujer» en «mi mujer Ana»** se toma como sexo femenino del paciente; hoy solo lo detiene la
  tarjeta. Es el motivo de la pregunta de arriba.
- **Pisar trabajo ajeno.** En el checkout compartido, el WIP de otra sesión compila junto con este
  y `bin/obj` se corrompe con dos corridas simultáneas. El worktree del paso 0 evita las dos
  cosas. Nunca `git add` en el checkout principal.

### Convivencia con `agent-jobs-health.md`

- Parte A ya entró (`2d55f68`); la parte B sigue retenida. No hay conflicto textual: este plan toca
  `AgentRuntime.cs:634-640`, y `AgentWorker` empieza en `:1181`.
- No correr `test-backend.sh` a la vez en el mismo directorio. Ninguno de los dos toca
  `AgentHarness`.

## Archivos compartidos

Otra sesión trabaja en recordatorios S3/S4 de WhatsApp (Recepción) y en S3-H (Hospital). Lo que
comparte este plan con esa sesión:

| Archivo | Este plan | Recordatorios | Riesgo |
|---|---|---|---|
| `backend/AgentRuntime.cs` | solo `:634-640` (dos lambdas) | `reminded`/`TappedReminder` (`:34-35`, `:68-70`), consentimiento (`:108-110`), `ReminderReply` (`:403-440`), prompt (`:160`, `:202`) | **El único archivo de producción compartido.** Las regiones no se cruzan, pero si los recordatorios entran primero, las líneas se mueven. Anclar por símbolo (`"propose_registration"`, `"start_registration"`), no por número, y re-anclar antes del paso 3 |
| `backend.Tests/AgentGuardTests.cs` (`AgentHarness`, `:981-1100`) | lo usa, no lo cambia | lo usan `ReminderReplyTests`, `GeneralNumberTests`, `AttendanceTests` | si la otra sesión cambia el harness, `AgentEvidenceTests` compila contra la versión nueva |
| `backend.Tests/HospitalIdempotencyTests.cs` | siembra un mensaje en `Propose` (`:47`) | posible, si S3-H cambia la idempotencia del cliente | conflicto pequeño, en una sola línea del helper |
| `backend/Integrations/HospitalClient*.cs` | **no lo toca** | sí | ninguno |
| `backend/WhatsAppEndpoints.cs`, `backend/Program.cs` | **no los toca** | sí | ninguno |
| `backend/Migrations/**` | **no las toca**: este plan no tiene migración | posible | ninguno, pero una migración ajena entra en la misma suite |
| `frontend/**`, i18n | **no los toca** | posible | ninguno |
| `backend/AppointmentReminders*.cs`, `ReminderEndpoints.cs` | **no los toca** | sí | ninguno |

Las dos sesiones comparten además la base de pruebas (`docker compose exec db`) y, en el checkout
principal, `bin/obj`. Por eso cada una trabaja en su propio worktree y no se lanzan dos
`test-backend.sh` desde el mismo directorio.

## 6. EV-8 — DECIDIDO («sí a las dos», propietario, 2026-10-23) y HECHO

- Hecho: `Intake.Wrote` devuelve siempre falso para el campo 3, así que el sexo queda nulo en el
  filtro que comparten las dos lambdas (`StartIntake`). `SexOf` ya no se usa en `Wrote`; sigue en
  `case 4`.
- Gracias a S2, el formulario pregunta solo el sexo y conserva el contacto (`AgentIntake.cs:31-34`).
  No hay que tocar `Intake.Start` ni `Answer`, y EV-10 no se roza. La duda del plan original sobre
  dos variantes del formulario ya no existe.
- Prueba (spec EV-8): `AgentEvidenceTests.TheSexIsAlwaysTappedNeverTakenFromTheModel`, dos casos:
  el de la spec («soy hombre, mi contacto es mi mujer Ana»), y uno con «hombre» como única palabra
  de sexo. El modelo pasa `sex`. Assert: `h.Sent[^1]` contiene «documento de identidad» y «me falta
  uno»; el JSON del `intake` trae `sex: null`; cero propuestas. El caso de la spec ya salía verde
  con P-1 (dos palabras de sexo distintas: se retenía); el rojo lo da el segundo caso.
- Asserts a los que se añadió el turno del toque «f» (el assert final no cambia):
  `AgentHandsTheFormWhatItAlreadyKnows`, `ARelationshipSaid…`, los dos usuarios de
  `ProposeAndGetCode`, `FirstAppointment…` (`:85`), los tres de `HospitalIdempotencyTests` y el
  caso `true` de `AFormThatStartsWithKnownDataReadsLikeAChat`. Este último ya pregunta el sexo y no
  cambió. También `AgentEvidenceTests.ADateAndAPhoneAreComparedAsADateAndAPhone` (EV-4(a)(d)), que
  es nueva.
- Si la respuesta es «el médico necesita otro dato», cambian la pregunta y las opciones
  (`AgentIntake.cs:68`, `:84`) y el valor que se envía a Hospital. Eso es una spec nueva, no este
  plan.

## 7. Lo que NO se hace

- **Retirar `propose_registration` de `Tools()`.** Dejaría verdes en vacío
  `InvalidRegistrationIsNeverProposed`, `LinkedPatientIsNeverRegisteredAgain`, los tres de
  `HospitalIdempotencyTests` y unas 50 evals con `noTools`.
- **Devolver error al modelo cuando falta evidencia.** El texto final del modelo («Revisa tus datos
  y confirma») llegaría al paciente sin tarjeta. Por eso la lambda cae al formulario.
- **Poner el filtro dentro de `ProposeRegistration` o de `Intake.Start`.** Los llaman el formulario
  sin modelo con respuestas tecleadas (`IntakeStep`, `:911`) y «Otro horario» con los datos ya
  revisados de la tarjeta (`:498`).
- **Un regex de fechas nuevo.** `Date` ya busca sin anclas desde intake-voice; se reutilizan sus
  cuatro regex.
- Tipo `Evidence`, parámetro nuevo para el modelo, cambio de prompt o cambio de harness.
- Coincidencia de teléfono por sufijo («+503 70000007» frente a «7000-0007»): se pregunta.
- Correr evals o `AgentLiveJourney`.

## Asunciones del plan, a confirmar

- ~~**P-1 (sexo sin EV-8).**~~ **Desaparecida** con EV-8 (2026-10-23). EV-4 no dice cómo se compara el sexo, y EV-3 exige tomarlo si consta.
  Se usa el vocabulario del paso 4 (`AgentIntake.cs:68`), incluidos «f» y «m», que son el toque del
  botón. Desaparece si el propietario contesta «sí a las dos».
- **P-2 (nombres).** «Palabra completa» se lee palabra por palabra en cualquier mensaje de la
  conversación, no como frase contigua. El modelo puede recombinar palabras escritas; eso lo detiene
  la tarjeta, como admite «Límites conocidos».


## Ejecución 2026-10-23

Rama `feat/agent-evidence` desde `origin/main` `43b5eda`, en worktree privado. Servicio `db` de
`recepcion` ya estaba arriba (se dejó arriba). Ni evals ni `AgentLiveJourney`.

**Paso 0 — línea base** (`scripts/test-backend.sh`, sin filtro):

```
Passed!  - Failed:     0, Passed:  1356, Skipped:     0, Total:  1356, Duration: 3 m 24 s - Recepcion.Tests.dll (net10.0)
```

**Paso 1 — montajes** (`~AgentIntakeFlowTests|~AgentIntakeTests|~AgentVoiceTests|~HospitalIdempotencyTests`),
mismos asserts, árbol verde:

```
Passed!  - Failed:     0, Passed:    87, Skipped:     0, Total:    87, Duration: 6 s - Recepcion.Tests.dll (net10.0)
```

**Paso 2 — rojos** (`~AgentEvidenceTests`, antes de tocar producción). Nació verde
`ADateAndAPhoneAreComparedAsADateAndAPhone` (EV-4(a)(d)). EV-1 salió rojo por el conteo: el
cuerpo trae **16** herramientas, no 17 (`hospital_agreement` sólo se ofrece con convenios
activos); el assert se corrigió a 16 con ese comentario.

```
  Failed AgentEvidenceTests.ANameIsComparedWordByWordNeverApproximately [469 ms]
   Assert.Contains() Failure: Sub-string not found
String:    "Revisa tus datos:\n\n*Irene Sintética March"···
Not found: "¿Y tus apellidos?"
  Failed AgentEvidenceTests.WhatThePatientNeverWroteIsAskedAndKeptNowhere [11 ms]
   Assert.Contains() Failure: Sub-string not found
String:    "Revisa tus datos:\n\n*Irene Sintética Mora "···
Not found: "¿Cuál es tu nombre?"
  Failed AgentEvidenceTests.AnAdultDateOnlyTheModelGaveIsAskedAndKeptNowhere [14 ms]
   Assert.Contains() Failure: Sub-string not found
String:    "Revisa tus datos:\n\n*Irene Sintética Mora "···
Not found: "¿Cuál es tu fecha de nacimiento?"
  Failed AgentEvidenceTests.NoToolOffersTheModelAConfidenceAScoreOrASource [27 ms]
   Assert.Equal() Failure: Values differ
Expected: 17
Actual:   16
  Failed AgentEvidenceTests.TwoBirthDatesThePatientWroteAreBothWithheld [24 ms]
   Assert.Contains() Failure: Sub-string not found
String:    "Ya tengo parte de tus datos; me faltan 3."···
Not found: "¿Cuál es tu fecha de nacimiento?"
  Failed AgentEvidenceTests.ADateIsReadDayFirstAsTheFormReadsIt [15 ms]
   Assert.Contains() Failure: Sub-string not found
String:    "Revisa tus datos:\n\n*Irene Sintética Mora "···
Not found: "¿Cuál es tu fecha de nacimiento?"
  Failed AgentEvidenceTests.ASexThePatientNeverWroteIsAsked [17 ms]
   Assert.Contains() Failure: Sub-string not found
String:    "Revisa tus datos:\n\n*Irene Sintética Mora "···
Not found: "documento de identidad"
  Failed AgentEvidenceTests.AProposedRegistrationThePatientNeverWroteLeavesNoCardAndReachesNoHospital [18 ms]
   Assert.Empty() Failure: Collection was not empty
Collection: [PendingAction { Body = "{\"action\":\"register\",\"givenNames\":\"Irene Si"···, Code = "677B71", ConsumedAt = null, ConversationId = a1c51530-2529-40f9-8b1e-2162fdc9f7e4, CreatedAt = 2026-10-24T05:03:48.2106806+00:00, ··· }]
  Failed AgentEvidenceTests.AMinorIsNotMadeAnAdultByTheModelsDate [16 ms]
   Assert.Contains() Failure: Sub-string not found
String:    "Revisa tus datos:\n\n*Diego Sintético Prueb"···
Not found: "¿Cuál es tu fecha de nacimiento?"
Failed!  - Failed:     9, Passed:     1, Skipped:     0, Total:    10, Duration: 1 s - Recepcion.Tests.dll (net10.0)
```

EV-1 con `string? confidence = null` temporal en `start_registration` (retirado después):

```
  Failed AgentEvidenceTests.NoToolOffersTheModelAConfidenceAScoreOrASource [383 ms]
   Assert.DoesNotContain() Failure: Filter matched in collection
                                                                                                                                                            ↓ (pos 33)
Collection: [···, "start_registration.emergencyContactName", "start_registration.emergencyContactRelationship", "start_registration.emergencyContactPhone", "start_registration.confidence", "hospital_services.words"]
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 1 s - Recepcion.Tests.dll (net10.0)
```

**Paso 3 — verde.** `~AgentEvidenceTests`, luego `~AgentIntake`, luego la suite completa:

```
Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 1 s - Recepcion.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    56, Skipped:     0, Total:    56, Duration: 5 s - Recepcion.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:  1366, Skipped:     0, Total:  1366, Duration: 1 m 21 s - Recepcion.Tests.dll (net10.0)
```

**Paso 4 — EV-8.** Rojo de `TheSexIsAlwaysTappedNeverTakenFromTheModel` (el caso de la spec ya
salía verde con P-1; el rojo es el de «hombre» solo):

```
  Failed AgentEvidenceTests.TheSexIsAlwaysTappedNeverTakenFromTheModel(said: "Soy Pedro Sintético Prueba, hombre, nací el 2 de f"···) [1 s]
   Assert.Contains() Failure: Sub-string not found
String:    "Revisa tus datos:\n\n*Pedro Sintético Prueb"···
Not found: "documento de identidad"
Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2, Duration: 4 s - Recepcion.Tests.dll (net10.0)
```

Verde, suite completa sin filtro, con el turno del toque «f» añadido a las pruebas de §6:

```
Passed!  - Failed:     0, Passed:  1368, Skipped:     0, Total:  1368, Duration: 1 m 46 s - Recepcion.Tests.dll (net10.0)
```

Conteo final: 1356 + 12 nuevas (10 de los pasos 2–3, 2 casos de EV-8) = **1368**.

### Desviaciones

- EV-1 afirma 16 herramientas, no 17 (ver arriba).
- El filtro vive en un método `StartIntake` de `AgentRuntime` que llaman las dos lambdas, en vez
  de duplicar el cuerpo en cada una.
- EV-4(c) afirma el `intake` guardado (apellidos nulos; fecha y teléfono conservados) en vez de
  «me falta uno»: con EV-8 faltan dos (apellidos y sexo).
- La spec (`agent-evidence.md`, A-2) sigue diciendo «pendiente de confirmar»; no se tocó.
