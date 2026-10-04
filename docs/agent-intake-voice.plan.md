# Plan — El formulario habla como la gente (S1) y salta a lo que falta (S2)

Spec: [agent-intake-voice.md](agent-intake-voice.md). Medido el 2026-10-04 contra `main` local
`4e05f77`; solo lectura, no se ejecutó ninguna prueba.

> **Implementado y corregido (2026-10-04)** en el worktree `scratchpad/wt-voice`, sin commit: suite
> `406/0`. Tras la revisión adversaria el código se aparta de este plan en dos puntos, que la spec
> ya recoge: (1) INV-H-4 — sin fecha de nacimiento aceptada no se toma nada posterior, así que el
> assert de H-3 vuelve a `Assert.Null(state.Sex)` y no cambia de expectativa; (2) A-8 — con
> apellidos ya conocidos la pregunta 1 dice «Solo nombres, sin apellidos.».

**Estado: implementable.** B-1 no bloquea S1 ni S2. Tres asunciones del plan (P-1…P-3, al final) y
un hueco que la spec no vio (`AgentLiveJourney.cs`).

## 0. Estado del árbol

- `AgentRuntime.cs` ya no está sucio: lo que la spec midió sin confirmar entró como `4e05f77`.
  `main` local va 10 delante y 4 detrás de `origin/main`.
- Ese commit trajo la espera tras derivación (`waitingSince`, `HandedOverUnanswered`,
  `StillWaiting`) y el orden de horarios por preferencias: +35 líneas en `:37` (+3), `:47` (+8),
  `:262` (+23), `:416` (+1).
- Las líneas citadas por la spec siguen vigentes; dependen de ese commit solo en la numeración.
- `AgentIntake.cs` (`:14, :23, :25, :29, :41, :43, :56-62`) no cambió. `IntakeStep` se declara en
  `:468`; `:470` es la llamada a `Answer`.
- Trabajo ajeno sin confirmar que compila en la misma corrida y no se toca:
  `backend/HospitalEndpoints.cs`, `Integrations/HospitalClient.cs`, `Program.cs`,
  `InstallationEndpoints.cs`, `backend.Tests/InstallationTests.cs`,
  `AppointmentCancellationTests.cs`, y trece archivos de frontend.

### Nombres de prueba y líneas de assert

Los 20 nombres que cita la spec existen. Los seis números de línea coinciden:

| Cita | Archivo | Contenido |
|---|---|---|
| `:88` | `AgentIntakeFlowTests.cs` | `Assert.Contains("Paso 1 de 7", await Say("AGENDAR"))` |
| `:97` | `AgentIntakeFlowTests.cs` | `Assert.Contains("ya tienes tu expediente", registered)` |
| `:147` | `AgentIntakeFlowTests.cs` | `Assert.Contains("Paso 4 de 7", prompt)` |
| `:206` | `AgentIntakeFlowTests.cs` | `Assert.Contains("Paso 2 de 7", Assert.Single(h.Sent))` |
| `:232` | `AgentInteractionTests.cs` | `Assert.Contains("Paso 1 de 7", Assert.Single(h.Sent))` |
| `:121` | `AgentVoiceTests.cs` | `Assert.Contains("Paso 1 de 7", sent)` |

Correcciones a la spec:

- **Hueco: `backend.Tests/AgentLiveJourney.cs` no aparece en la spec** y afirma los textos viejos
  en `:64`, `:178` y `:185`. Es `Category=Live`: la suite normal no lo corre, así que S1 lo dejaría
  rojo en silencio hasta el próximo `scripts/live-agent.sh`. El plan lo incluye.
- `AgentHarness` vive en `AgentGuardTests.cs:471`.
- `EmergencyContactIsRegistrationDataNotAnEmergency` (`AgentGuardTests.cs:112`) llama a `Inbound`
  sin `registering`: confirma que no cubre el acoplamiento con `:46`.

### Cómo se corren las pruebas

- Worktree privado: `git -C <Recepcion> worktree add <scratchpad>/wt-voice main`, y `.env`
  enlazado. `docker-compose.yml` fija `name: recepcion`, así que el script funciona desde ahí.
- Con cwd en la raíz del worktree: `scripts/test-backend.sh "<filtro>"`.
- Filtro de trabajo (nunca `~Agent` a secas):
  `FullyQualifiedName~AgentIntakeFlowTests|FullyQualifiedName~AgentIntakeTests|FullyQualifiedName~AgentVoiceTests|FullyQualifiedName~AgentInteractionTests|FullyQualifiedName~AgentMemoryTests|FullyQualifiedName~AgentTraceTests|FullyQualifiedName~AgentEvalCasesAreValid`
- Cierre de cada entrega: sin argumento (excluye `Eval` y `Live`).

## 1. Archivos que cambian

| Archivo | S1 | S2 |
|---|---|---|
| `backend/AgentIntake.cs` | literales de `:14`, `:29`, `:56-62` | `Start(today, known)` (`:18-30`), ramas aceptadas de `Answer` (`:37-48`), dos ayudantes privados |
| `backend/AgentRuntime.cs` | `:145` (prompt) y `:634` (cierre), reemplazo en la misma línea | nada |
| `backend.Tests/AgentIntakeFlowTests.cs` | `:67-71`, `:88`, `:97`, `:147`, `:206`; 2 pruebas nuevas | `:155`; 4 pruebas nuevas |
| `backend.Tests/AgentVoiceTests.cs` | `:121`, `:127-136` | — |
| `backend.Tests/AgentInteractionTests.cs` | `:232` | — |
| `backend.Tests/AgentMemoryTests.cs` | 1 prueba nueva (reutiliza `Model`, `Said`, `seen`) | — |
| `backend.Tests/AgentLiveJourney.cs` | `:64`, `:178`, `:185` (no se corre) | — |
| `evals/agent/cases/registro-003-datos-incompletos-no-se-suponen.json` | `:31` | — |
| `evals/agent/cases/urgencia-004-cliente-nuevo-de-noche.json` | `:23` | — |
| `docs/agent-evidence.md`, `docs/reception-agent.md`, `docs/agent-evidence.plan.md` | enmiendas (§7) | — |

No se tocan: `AgentGuard.cs`, `AgentHarness`, `ProposeRegistration` (`:453-465`), `IntakeStep`
(`:468-478`), el bloque `:223-240`, `Tools()`, `AgentWorker`, migraciones.

### Textos exactos de S1 (`AgentIntake.cs`)

| Línea | Texto nuevo |
|---|---|
| `:14` (lead de `Start()`) | `Con gusto. Para darte cita necesito registrarte: son 7 datos cortos.\n\n` |
| `:29` (lead con datos) | `Ya tengo parte de tus datos; me faltan {N}.\n\n`; con N = 1, `…me falta uno.\n\n`. En S1, `N = Steps + 1 - Step` |
| `:56` | `¿Cuál es tu nombre?\nSolo nombres; los apellidos te los pido enseguida.` |
| `:57` | `¿Y tus apellidos?` |
| `:58` | `¿Cuál es tu fecha de nacimiento?\nPor ejemplo: 12/03/1990` |
| `:59` | `¿Qué sexo aparece en tu documento de identidad?\nEs un dato que pide el hospital para registrarte.` (botones sin cambio) |
| `:60` | `¿Quién es tu contacto de emergencia?\nEscribe su nombre completo.` |
| `:61` | `¿Qué parentesco tiene contigo tu contacto de emergencia?\nPor ejemplo: madre, hermano, pareja.` |
| `:62` | `Último dato:\n¿Cuál es el teléfono de tu contacto de emergencia?` |

`AgentRuntime.cs`:

- `:145`: «sexo registral (femenino o masculino)» pasa a «el sexo que aparece en su documento de
  identidad (femenino o masculino)».
- `:634`: «, ya tienes tu expediente.\n» pasa a «, ya te registré.\n».

Los leads de error («No pude leer…», «Necesito una de las dos opciones.») no cambian.

### Forma de S2 (sin tipos nuevos)

`Step` sigue siendo «el dato que se está preguntando», así que el JSON de `intake` e `IntakeStep`
no cambian. Dos ayudantes privados, como métodos para que no entren al JSON ni a la igualdad del
record:

```csharp
string?[] Data() => [GivenNames, FamilyNames, BirthDate, Sex, EmergencyName, EmergencyRelationship, EmergencyPhone];
Intake Next() => this with { Step = Array.IndexOf(Data(), null) is >= 0 and var i ? i + 1 : Steps + 1 };
```

- **`Answer`:** cada rama aceptada cambia `this with { Step = n + 1, X = v }` por
  `(this with { X = v }).Next()`. La rama 7 pasa a
  `Ask((this with { EmergencyPhone = … }).Next())`; `Ask` ya devuelve prompt vacío si `Step > 7`.
  La rama del menor (`Step = -1`) no cambia.
- **`Start(today, known)`:** recorre los siete, y en cada uno: si está en blanco, `continue` (hoy
  es `break`); `var asked = state with { Step = i + 1 }; var read = asked.Answer(value, today).State;`
  si `read.Minor`, devuelve `(read, "", null)`; si `read != asked`, `state = read` (la igualdad de
  record distingue aceptado de rechazado: un rechazo devuelve `this`).
- Al terminar: `state = state.Next()` y `left = state.Data().Count(x => x is null)`. Si
  `left == Steps`, devuelve `Start()`. Si no, `Ask(state, lead)` con el lead de V-7 usando `left`
  (reemplaza el `Steps + 1 - Step` de S1). Completo: `Ask` descarta el lead y devuelve prompt vacío.

## 2. Orden del trabajo

**S1 y S2 se entregan por separado, en el mismo worktree y en serie; S1 primero.** S1 es lo que
pidió el propietario y no cambia comportamiento. S2 cambia el recorrido, trae un cambio declarado
(H-3) y sus pruebas afirman los textos de S1. No van en paralelo: ambas editan `Start` y `Ask`.

**Paso 0 — línea base.** Suite sin argumento en el worktree. El conteo es la puerta (B).

**Entrega S1**

1. **Rojos.** Cambiar los asserts de §4 y añadir las tres pruebas nuevas de S1. Correr el filtro y
   pegar cada mensaje.
2. **Verde.** Literales de `AgentIntake.cs` y las dos líneas de `AgentRuntime.cs`. Filtro, luego
   suite completa: **B + 3**.
3. **Mutaciones** de las que nacen verdes (V-5, V-6 en parte, V-9, V-10): aplicar, pegar el rojo,
   revertir.
4. **Datos y docs.** Las dos cadenas de eval (`AgentEvalCasesAreValid` en verde),
   `AgentLiveJourney.cs`, enmiendas de §7.

**Entrega S2**

5. **Rojos.** `:155` y las cuatro pruebas nuevas. Correr y pegar.
6. **Verde.** `Data()`, `Next()`, `Answer`, `Start`. Filtro, luego suite completa: **B + 7**.
7. **H-6.** Confirmar que las cuatro pruebas «sin modificar» pasaron y que `git diff` no las toca.

### Orden entre los tres planes

1. **`agent-jobs-health`**: independiente. Toca `AgentWorker` (`:651-686`) y
   `AgentIntegrationTests.cs`; este plan no toca ninguno de los dos.
2. **`agent-intake-voice` S1, luego S2.**
3. **`agent-evidence` EV-1…EV-7, EV-9, EV-10.** Después de S2, no en paralelo: choca en
   `AgentIntake.cs:43` (extrae `SexOf` de la rama que S2 edita) y en `AgentIntakeFlowTests.cs`.
4. **EV-8**, tras el visto bueno de A-2.

Citas que se corren:

- `wt-jobs` está en `139f4e2`, un commit detrás de `main`: tiene que rebasar sobre `4e05f77` antes
  de medir su conteo (le faltan `AgentWaitingTests` y `AgentMetricsTests`). Sus citas suben +35:
  `AgentWorker :616-651`→`:651-686`.
- `agent-evidence.plan.md` cita contra `139f4e2`: lambdas `:357`, `:358-363`→`:391`, `:392-397`;
  bloque `:212-229`→`:223-240`; `ProposeRegistration :418-430`→`:453-465`; prompt
  `:106-159`→`:117-170`.
- Este plan no mueve ninguna línea de `AgentRuntime.cs`. En `AgentIntake.cs`, S2 añade unas 4
  líneas antes de `Answer`.

Lo que evidence debe absorber después de este plan: todos sus «Paso N de 7» pasan a afirmar la
pregunta; su riesgo «lo que sigue a un dato retenido también se pregunta» deja de ser cierto; la
prueba de H-5 entra en su lista de montajes; su §6 (EV-8) se simplifica: el coste ya es un toque.

## 3. Pruebas por criterio

### S1

| Criterio | Prueba | Rojo esperado, o mutación |
|---|---|---|
| V-1 | `SexIsAnsweredWithButtons` ampliada. `Assert.Equal` con el texto exacto de `:59`, `DoesNotContain("registral")`, ids `["f","m"]`. | `Assert.Equal() Failure: Strings differ`, con «*Paso 4 de 7*\n¿Cuál es tu sexo registral?» como actual |
| V-2 | Sin tocar: `AnAnswerThatDoesNotFit…`, `SevenAnswersCompleteARegistration`, `AgentIntakeTests.NewClientIsRegisteredOnlyAfterConfirmingAndCanThenBook` | Regresión. `SevenAnswers…` afirma `Contains("nombres")` sobre `Start()`: sigue cierto por «Solo nombres» |
| V-3 | `EveryStepOfRegisteringReadsLikeAChat` ampliada: `Assert.All(h.Sent, m => { DoesNotContain("registral", m); DoesNotContain("Paso ", m); })` | `Assert.All() Failure`, el primero por «Paso 1 de 7» |
| V-4 | `:88`, `:232`, `:121` (ver §4) | `Not found: "son 7 datos cortos"` |
| V-5 | Misma prueba de V-3: `h.Sent[1]` «¿Y tus apellidos?», `[2]` «fecha de nacimiento», `[4]` «¿Quién es tu contacto de emergencia?», `[5]` «parentesco tiene contigo», `[6]` «teléfono de tu contacto de emergencia» | **Nace verde.** Mutación: quitar «contigo» de `:61` |
| V-6 | Nueva pura `OnlyTheLastQuestionSaysItIsTheLast`: el prompt de la pregunta 7 `StartsWith("Último dato:\n")` y ningún otro contiene «Último dato» | `Assert.StartsWith() Failure`. La mitad «ningún otro» nace verde: mutación, poner el prefijo también en `:60` |
| V-7 | `:147` y `:206` (ver §4) | `Not found: "me faltan 4"`; `Not found: "documento de identidad"`. En `:206` el rojo propio es `"me faltan 6"` (P-3) |
| V-8 | `:97` | `Not found: "ya te registré"` |
| V-9 | `Assert.All(h.Sent, ReadsLikeAChat)`, sin tocar | **Nace verde.** Mutación: unir las dos líneas de `:59` y el lead de `:14` en una |
| V-10 | Nueva en `AgentIntakeFlowTests`: `ALabelledEmergencyContactInTheFormIsNotAnEmergency`. AGENDAR, cuatro respuestas, «Emergencia: Carlos Sintético», con `NoModel`. `Status == "agent"`, ninguna actividad `handoff`, y `h.Sent[^1]` contiene «contacto de emergencia» | **Nace verde.** Mutación: quitar «contacto de emergencia» de `:60`. Rojo: `Expected: "agent" Actual: "human"` |
| V-11 | Nueva en `AgentMemoryTests`: `ThePromptAsksForTheSexOnTheIdentityDocument`. `Said(seen[0], "system")` no contiene «registral» y contiene «documento de identidad» | `Assert.DoesNotContain() Failure`. Hueco de la spec, se mantiene: que el modelo use esas palabras solo lo ven las evals |
| V-12 | `AgentEvalCasesAreValid` (solo forma) | Sin rojo posible. Hueco de la spec, se mantiene |

### S2

| Criterio | Prueba | Rojo esperado |
|---|---|---|
| H-1 | Nueva pura `TheFormTakesWhatItKnowsWhereverItIs`: la llamada literal de la spec. `Step == 4`; prompt `StartsWith("Ya tengo parte de tus datos; me falta uno.\n\n¿Qué sexo aparece")`; `choices` no nulo; el trío del contacto igual a `("Carlos Sintético","hermano","70000001")` | `Assert.StartsWith() Failure` por «me faltan 4.»; quitado ese, `Assert.Equal()` con `(null, null, null)` |
| H-2 | Nueva pura `AfterAnAcceptedAnswerTheNextMissingDatumIsAsked`: `new Intake(4, "Rosa Sintética", "Prueba", "1985-01-08", null, "Carlos Sintético", "hermano", "70000001").Answer("Femenino", Today)` da `Complete` | `Assert.True() Failure`; el estado real tiene `Step == 5` |
| H-3 | `AKnownAnswerThatDoesNotFitIsAskedNotKept`, `:155` (ver §4) | `Expected: "female" Actual: null` |
| H-4 | Nueva pura `AKnownMinorBirthDateEndsTheFormWhateverElseIsMissing`: `Intake.Start(Today, null, null, "01/01/2010").State.Minor` | `Expected: True Actual: False`. Sin tocar: las tres de menores |
| H-5 | Nueva por `Run`: `WhenOnlyTheSexIsMissingThePatientTapsOnceAndGetsTheCard`. Modelo: `ToolCall("start_registration", seis datos sin sex)` + `Reply("Ok")`. Luego `Say("Femenino")` con `NoModel` | Primera mitad nace verde tras S1. Segunda: `Not found: "Contacto de emergencia: Carlos Sintético"` |
| H-6 | Sin tocar: `SevenAnswersCompleteARegistration`, `ARelationshipSaidInFrontOfTheContactsNameIsNotAskedAgain`, `AgentTraceTests.TheFormThanksByName`, `RegistrationFormInProgressNeverShowsInTheHistory` | Regresión |

H-2 construye el estado a mano para que su rojo no quede tapado por el de H-1. INV-H-2 queda
cubierto por H-3 (`BirthDate == null`).

## 4. Asserts existentes que cambian de literal

Ninguno se borra ni se debilita. Donde había un `Contains` ahora hay dos.

| # | Lugar | Hoy | Pasa a |
|---|---|---|---|
| 1 | `AgentIntakeFlowTests.cs:88` | `Contains("Paso 1 de 7", await Say("AGENDAR"))` | `var first = await Say("AGENDAR");` + `Contains("son 7 datos cortos", first)` + `Contains("¿Cuál es tu nombre?", first)` |
| 2 | `AgentIntakeFlowTests.cs:97` | `Contains("ya tienes tu expediente", registered)` | `Contains("ya te registré", registered)` |
| 3 | `AgentIntakeFlowTests.cs:147` | `Contains("Paso 4 de 7", prompt)` | `Contains("me faltan 4", prompt)` + `Contains("documento de identidad", prompt)`; `NotNull(choices)` se queda |
| 4 | `AgentIntakeFlowTests.cs:206` | `Contains("Paso 2 de 7", Assert.Single(h.Sent))` | `Contains("¿Y tus apellidos?", …)` + `Contains("me faltan 6", …)` (P-3) |
| 5 | `AgentInteractionTests.cs:232` | `Contains("Paso 1 de 7", Assert.Single(h.Sent))` | los dos `Contains` de la fila 1 sobre `Assert.Single(h.Sent)` |
| 6 | `AgentVoiceTests.cs:121` | `Contains("Paso 1 de 7", sent)` | los dos `Contains` de la fila 1; `DoesNotContain("parentesco")` se queda |
| 7 | `AgentLiveJourney.cs:178` | `Contains("Paso 1 de 7", await Turn("AGENDAR"))` | `Contains("son 7 datos cortos", …)` |
| 8 | `AgentLiveJourney.cs:64`, `:185` | `Contains("ya tienes tu expediente", registered)` | `Contains("ya te registré", registered)` |
| 9 (S2) | `AgentIntakeFlowTests.cs:155` | `Assert.Null(state.Sex)` | `Assert.Equal("female", state.Sex)`, y su comentario. `Assert.Equal(3, state.Step)` y `:156` se quedan |

Las filas 7 y 8 no se pueden verificar en la puerta: literal cambiado, sin correr.

### V-12: cadenas exactas de eval

| Archivo | Línea | Hoy | Pasa a |
|---|---|---|---|
| `evals/agent/cases/registro-003-datos-incompletos-no-se-suponen.json` | `:31` | `"Paso \\d de 7"` | `"son 7 datos cortos\|Ya tengo parte de tus datos; me faltan? "` |
| `evals/agent/cases/urgencia-004-cliente-nuevo-de-noche.json` | `:23` | `"Paso \\d de 7"` | la misma |

Se usa la frase entera del servidor (P-2): «me falta» a secas la puede escribir el modelo. Las
evals no se corren.

## 5. Riesgos

- **El paso más peligroso es el 6 (`Answer` y `Start`).** Es el único camino por el que entra un
  registro sin modelo. Si `Next()` se equivoca, un formulario se da por completo con un dato nulo y
  `IntakeStep` (`:476`) lo pasa con `!` a `ProposeRegistration`; `Rules.Required` lanzaría y el
  trabajo acabaría en `failed`. Lo cubren `SevenAnswers…`, H-2 y H-5.
- **Igualdad de record como detector de «aceptado».** Depende de que las ramas de rechazo devuelvan
  `this`. Si alguien cambia una a `this with {…}`, un valor rechazado contaría como aceptado. H-3
  lo detecta para la fecha.
- Verde en vacío en `ARelationshipSaid…`: su `DoesNotContain("parentesco tiene")` depende del
  literal de `:61`. S1 conserva la frase y V-5 la fija en positivo.
- Verde en vacío en `AgentRuntime.cs:46`: depende de que las preguntas 5–7 digan «contacto de
  emergencia». V-5 y V-10 lo fijan.
- **Dos roces de texto entre S1 y S2** que la spec no resuelve: con apellidos conocidos y nombre
  faltante, la pregunta 1 dirá «los apellidos te los pido enseguida» y no los pedirá; con solo el
  teléfono faltante, el primer mensaje dice «me falta uno» y «Último dato:» a la vez. Poco
  probables; ninguno pide un dato de más. Ver P-1.
- A-6 (formulario a medias en el despliegue): S2 no cambia el significado de `Step` guardado.
- Línea base sin medir y trabajo ajeno compilando en la misma corrida: un rojo de build no es de
  este plan. El worktree lo aísla.
- Evals sin correr: el cambio de `:145` puede mover cómo pide el modelo los datos. Sin puerta.

## 6. Lo que NO se hace

- Tocar la tarjeta (`:463`), `:457`, o «expediente» en `:76, :107, :495, :625, :628`.
- Cambiar el vocabulario aceptado del sexo, los botones, el orden de las preguntas, o qué llega a
  Hospital.
- Tipo nuevo, `enum` de campos o propiedad pública en `Intake`: dos métodos privados bastan.
- Tocar `IntakeStep`, `:100` o `:223-240`: S2 cabe entero en `AgentIntake.cs`.
- Guardar el valor rechazado, o rellenar desde `contact.Name`, memoria o `recall`.
- Adaptar el texto de la pregunta 1 a si ya hay apellidos, o quitar «Último dato:» cuando el lead
  ya dijo «me falta uno»: la spec fija los textos.
- Los 10 historiales de eval con «sexo registral», el `danger` de `registro-104`, el comentario de
  `AgentIntakeFlowTests.cs:50` y el texto simulado del modelo en `AgentVoiceTests.cs:115`: ninguno
  llega al paciente.
- `reception-agent.md` criterio 17 (`:37`): la spec solo enmienda el 34.
- Correr `eval-agent.sh` o `live-agent.sh`.
- S3 (`agent-slot-first.md`): es borrador.
- `git add`, commit o cambio alguno en el checkout compartido.

## 7. Enmiendas de documentos

- `docs/agent-evidence.md` EV-2 y EV-8: «Paso 1 de 7» y «Paso 4 de 7» pasan a «¿Cuál es tu
  nombre?» y «documento de identidad».
- `docs/agent-evidence.md` EV-10: pasa a referirse al formulario tal como queda tras esta spec.
- `docs/agent-evidence.plan.md`: sus «Paso N de 7» de §4, el riesgo del «primer hueco», §6, y los
  números de línea de §2.
- `docs/reception-agent.md:54` (criterio 34): «sexo registral con botones» pasa a «sexo del
  documento con botones».

## Asunciones del plan, a confirmar

- **P-1.** «Último dato:» va pegado a la pregunta 7, como lo prueba V-6. Con S2, un formulario que
  termina en otra pregunta no lo anuncia. Es redacción, no clínica.
- **P-2.** El patrón de eval usa las frases completas del servidor en vez de «me faltan?» suelto.
- **P-3.** `:206` añade `"me faltan 6"` además de lo que pide la spec.
