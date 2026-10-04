# Plan — Evidencia, no confianza (S1)

Spec: [agent-evidence.md](agent-evidence.md). Medido el 2026-10-04; solo lectura. La línea base de
la suite está sin medir y es el paso 0.

**Estado:** ejecutable para EV-1…EV-7, EV-9 y EV-10. EV-8 espera a A-2. Dos asunciones del plan
(P-1, P-2) al final.

> **Re-anclar antes de ejecutar (2026-10-04).** Este plan se midió contra `139f4e2` y antes de
> `agent-intake-voice` y `agent-slot-first`, que van primero. Al retomarlo: (1) las líneas de
> `AgentRuntime.cs` suben +35 (lambdas `:357-363` → `:391-397`; bloque `:212-229` → `:223-240`;
> `ProposeRegistration` → `:453-465`), y se mueven otra vez con S3; (2) todo «Paso N de 7» de §4
> pasa a afirmar la pregunta («¿Cuál es tu nombre?», «documento de identidad», etc.); (3) el
> formulario ya no corta en el primer hueco, salvo que falte la fecha de nacimiento: el riesgo
> «lo que sigue a un dato retenido también se pregunta» solo vale para la fecha, y §6 (EV-8)
> cuesta un toque; (4) las pruebas nuevas de voz que pasan datos por `start_registration` entran
> en la lista de montajes de §3.

## 0. Estado del árbol

- `AgentRuntime.cs` y `AgentMemory.cs` ya no están sucios: entraron en `139f4e2`. `main` local va
  9 delante / 4 detrás de `origin/main`; los 4 de atrás son versiones anteriores de commits
  reescritos, más `d5f3ff8 feat(web)`, que solo existe en `origin`.
- Ancla en `main` local (`139f4e2`), no en `origin/main`: `start_registration` solo vive ahí. La
  reconciliación con `origin` es del propietario al hacer push.
- Las líneas citadas por la spec siguen vigentes contra `139f4e2`.
- Trabajo ajeno sin confirmar en backend: `HospitalEndpoints.cs`, `Integrations/HospitalClient.cs`,
  `Program.cs`, `InstallationEndpoints.cs`, `InstallationTests.cs`,
  `AppointmentCancellationTests.cs`. Este plan no toca ninguno, pero compilan en la misma corrida.
- Recepción no tiene `AGENTS.md` ni `CLAUDE.md` de raíz.

### EV-1: el harness permite ver `tools` sin cambiarlo

- `h.Model(...)` descarta la petición (`AgentGuardTests.cs:513`).
- `AgentHarness.Fake` recibe el `HttpRequestMessage` completo, y `AgentMemoryTests.cs:16` ya clona
  el cuerpo entero; `Said` (`:18`) solo lee `messages`.
- La prueba nueva usa un `Fake` propio de tres líneas que guarda el cuerpo y lee `tools`.
- **Sin verificar:** que el cuerpo traiga `tools[].function.parameters.properties`. Si no,
  `GetProperty("tools")` lanza y la prueba sale roja, no vacía.

### Cómo correr las pruebas

- `scripts/test-backend.sh "<filtro>"` con cwd en la raíz del árbol: usa `.env`, `$PWD:/src` y
  `docker compose exec db` relativos.
- Necesita docker, el servicio `db` arriba, la red `recepcion_default` y `.env` con
  `POSTGRES_PASSWORD`. Sin `TEST_DATABASE` el harness lanza (`AgentGuardTests.cs:484`): rojo, no salto.
- **El filtro propio reemplaza al de defecto.** `FullyQualifiedName~Agent` incluye `AgentEvals`,
  que con la `NVIDIA_API_KEY` de `.env` llama al modelo real (anti-criterio). Usar solo nombres de
  clase: `FullyQualifiedName~AgentEvidenceTests`, `FullyQualifiedName~AgentIntake`, o sin
  argumento para la suite completa.

## 1. Archivos que cambian

| Archivo | Cambio |
|---|---|
| `backend/AgentIntake.cs` | `Intake.Wrote(...)`, un regex sin anclas que localiza fechas en texto libre y las pasa a `Date`, y el mapa de sexo del paso 4 extraído a `SexOf` |
| `backend/AgentRuntime.cs` | solo `:357` y `:358-363` (las dos lambdas de registro) |
| `backend/AgentMemory.cs` | `Fold` (`:52`) pasa de privado a `internal` |
| `backend.Tests/AgentEvidenceTests.cs` | nuevo: EV-1, 2, 4, 5, 6, 7, 9 |
| `backend.Tests/AgentIntakeFlowTests.cs` | montaje de 4 pruebas |
| `backend.Tests/AgentIntakeTests.cs` | montaje del helper y de 3 llamadas |

No se tocan `AgentHarness`, `AgentGuard.cs`, el prompt (`:106-159`), `ProposeRegistration`
(`:418-430`), `IntakeStep`, `Confirm`, `Register`, las migraciones ni las evals.

### La única función nueva

```csharp
// AgentIntake.cs — field: 0..6 en el orden del formulario
public static bool Wrote(int field, string value, IReadOnlyCollection<string> said, DateOnly today)
```

| Campo | Regla |
|---|---|
| 0, 1, 4, 5 (nombres, apellidos, nombre y parentesco del contacto) | cada palabra de `AgentMemory.Fold(value)` está en el conjunto de palabras de `Fold` de los mensajes del paciente |
| 2 (fecha) | `Date(value)` existe, y las fechas válidas distintas halladas en `said` son exactamente esa una. Válida = `<= today` y `>= today-120`, como el paso 3. Más de una distinta retiene (EV-6) |
| 3 (sexo) | `SexOf(value)` existe, y los `SexOf(palabra)` no nulos del paciente son exactamente ese uno |
| 6 (teléfono) | `Rules.Phone(value)` es igual a `Rules.Phone` de alguna corrida con forma de teléfono en `said`. `ArgumentException` cuenta como falso |

En `AgentRuntime` las dos lambdas hacen lo mismo:

- `said` = cuerpos de `session` con `Sender == "patient"` (A-3).
- `startIntake = true; known =` los siete valores, cada uno nulo si está en blanco o `!Intake.Wrote(...)`.
- `propose_registration` deja de llamar a `ProposeRegistration` directamente y entra por el bloque `:212-229`.
- Si los siete constan, `Intake.Start` completa y sale la tarjeta de siempre (`:221`). Si no, el
  formulario pregunta y el texto del modelo se descarta.
- La inferencia «mi hija Sofía» (`:217-218`) ya lee texto del paciente y ve `known[4]` filtrado: no cambia.

## 2. Orden del trabajo

**Paso 0 — línea base.** Worktree privado desde `main` local
(`git -C <Recepcion> worktree add <scratchpad>/wt-evidence main`). `docker-compose.yml` fija
`name: recepcion`, así que el script funciona desde otro directorio; hay que enlazar `.env` dentro
del worktree. Suite sin filtro; el conteo es la puerta de este plan.

**Paso 1 — montajes (árbol verde, mismo conteo).** Siembra los mensajes de la sección 3. Hoy todo
se toma, así que siguen verdes. Demuestra que los textos sembrados pasan `AgentGuard.Inbound` y
`ForSomeoneElse` antes de tocar producción.

**Paso 2 — rojos.** `AgentEvidenceTests.cs`; correr y pegar cada mensaje de fallo. EV-1, EV-4(a) y
EV-4(d) nacen verdes.

**Paso 3 — verde.** `Intake.Wrote`, `SexOf`, el regex de fechas, `Fold` a `internal` y las dos
lambdas. `~AgentEvidenceTests`, luego `~AgentIntake`, luego la suite completa: conteo = línea base
+ pruebas nuevas. Para EV-1, añadir temporalmente `string? confidence = null` a
`start_registration`, pegar el rojo y retirarlo.

**Paso 4 (opcional, bloqueado por A-2) — EV-8.** Ver sección 6.

## 3. Montajes que cambian (los asserts no)

Los textos evitan «emergencia» fuera de «contacto de emergencia» y evitan registr-/agend-/cita
seguido de «a|para|de mi <pariente>» (`AgentGuard.cs:104`).

| Prueba | Mensaje del paciente a sembrar antes de `Run` |
|---|---|
| `AgentIntakeFlowTests.AgentHandsTheFormWhatItAlreadyKnows` | «Soy Irene Sintética Mora Prueba, mujer, nací el 9 de septiembre de 1979. Mi hija Sofía Sintética, 7000-0007.» |
| `…AMinorIsNoticedAsSoonAsTheBirthDateIsKnown` | «Me llamo Diego Sintético Prueba y nací el 01/01/2010.» |
| `…ARelationshipSaidInFrontOfTheContactsNameIsNotAskedAgain` | antes del mensaje que ya tiene: «Soy Irene Sintética Mora Prueba, femenino, nací el 9 de septiembre de 1979.» |
| `…AModelThatSaysNothingAfterStartingTheFormStillStartsIt` | «Me llamo Rosa Sintética.» |
| `AgentIntakeTests.ProposeAndGetCode` (`:34-38`) | primera línea del helper: «Soy Ana Sintética López Prueba, mujer, nací el 12/03/1990. Mi hermano Carlos Sintético, 70000001.» |
| `…FirstAppointmentOfARegisteredClientIsAFirstVisit` (`:83`) | el mismo, antes de `:83` |
| `…InvalidRegistrationIsNeverProposed` (`:119`) | `$"Soy Ana Sintética López Prueba, mujer, nací el {birthDate}. Mi hermano Carlos Sintético, 70000001."` |
| `…LinkedPatientIsNeverRegisteredAgain` (`:130`) | el mismo de Ana, antes de `:130` |

- El helper cubre dos pruebas más: `NewClientIsRegisteredOnlyAfterConfirmingAndCanThenBook` y
  `PossibleDuplicateIsNeverForcedAndReceptionIsOffered` quedan sembradas sin tocar su cuerpo.
- Tensión de la spec: EV-10 pide `PossibleDuplicate…` «sin modificar», y la lista de montajes
  incluye «todo lo que pasa por el helper». Sembrar dentro del helper satisface las dos lecturas.
- `:119` y `:130` pasarían sin siembra, pero en vacío: rechazaría la falta de evidencia, no la
  invalidez ni el vínculo.

## 4. Pruebas por criterio

Todas en `AgentEvidenceTests`, con `AgentRuntime.Run` real. `E` son los siete datos de Irene; `S`
es el mensaje de Irene de la sección 3.

| Criterio | Prueba | Rojo esperado hoy |
|---|---|---|
| EV-1 / INV-EV-4 | `Fake` que guarda la petición. `tools` tiene 13 entradas, incluye `start_registration` y `propose_registration`, y ninguna clave de `function.parameters.properties` casa con `(?i)confiden\|confianza\|score\|puntaje\|certeza\|url\|source\|fuente\|kind` | nace verde; con `confidence` temporal: falla nombrando `confidence` |
| EV-2 / INV-EV-1 | `start_registration(E)` solo con «Consulta sintética». `h.Sent[^1]` contiene «Paso 1 de 7»; cero `proposal:*` | `Assert.Contains` falla con «Revisa tus datos:…» como texto real |
| EV-2 (sexo) | `S` sin «mujer», modelo pasa `female`: «Paso 4 de 7» | sale la tarjeta |
| EV-3 | `AgentHandsTheFormWhatItAlreadyKnows` con `S`: una tarjeta, una `proposal:*` | nace verde. Discrimina por pareja con EV-2: misma llamada, solo cambia la siembra |
| EV-4(a)(d) | `S`, modelo pasa «09/09/1979» y «70000007»: tarjeta | nace verde; sería rojo con una comparación de cadenas ingenua |
| EV-4(b) | paciente «…nací el 03/04/1990…», modelo «1990-03-04»: «Paso 3 de 7», cero propuestas | sale la tarjeta |
| EV-4(c) | paciente «Soy Irene Sintética Marchetta…», modelo `familyNames` «Marchetti»: «Paso 2 de 7» | sale la tarjeta |
| EV-5(a) / INV-EV-2 | «Soy Irene Sintética Mora Prueba.», modelo pasa `E`: «Paso 3 de 7», cero propuestas | sale «Paso 5 de 7» |
| EV-5(b) / INV-EV-2 | «Soy Diego Sintético Prueba, nací el 01/01/2010.», modelo «1990-01-01»: «Paso 3 de 7», sin `handoff_offer`. Luego `h.Say("patient","01/01/2010")` y `Run`: hay `handoff_offer` | sale «Paso 4 de 7»: un menor avanza como adulto |
| EV-6 / INV-EV-3 | «Soy Irene Sintética Mora Prueba», «nací el 12/03/1990», «perdón, 12/03/1991»; modelo «1991-03-12»: «Paso 3 de 7», cero propuestas | sale «Paso 4 de 7» |
| EV-7 | `propose_registration(E)` solo con «Consulta sintética». Cero `proposal:*`; ningún `h.Sent` con «Revisa tus datos»; lista de `POST /v1/patients` vacía tras un segundo turno «sí» | `Assert.Empty` falla con una `proposal:XXXXXX` |
| EV-9 / INV-EV-5 | dentro de EV-2 y EV-5(a): ningún `Activity.Body` ni `h.Sent` contiene «Irene», «1979» ni «70000007» (según el caso) | el cuerpo de `proposal:*` contiene el valor |
| EV-10 | sin tocar y verdes: todas las de `AgentIntakeFlowTests` no listadas en la sección 3, y el cuerpo de `PossibleDuplicate…` | — |

El segundo turno «sí» de EV-7 existe porque, sin él, «cero POST» es cierto siempre.

**Huecos declarados**

- EV-11: sin puerta. No hay forma de medir desde aquí si la retención empeora `registro`.
- INV-EV-3: solo tiene criterio para la fecha (EV-6). Para nombres y teléfono, «dos valores
  distintos» no es decidible sin saber a qué campo responden las palabras.
- EV-8: fuera del plan ejecutable.

## 5. Riesgos

- **El paso más peligroso es el 3, en la lambda de `propose_registration` (`:357`).** Es el camino
  que termina creando expedientes, y cambia de ruta. Cambios visibles: un menor recibe la oferta de
  recepción en vez del texto del modelo; un dato inválido se pregunta en vez de devolver error al
  modelo. Los asserts actuales de `AgentIntakeTests` lo cubren.
- **Retención de más con modelo real.** El modelo corrige «Gonzales» a «González», o el paciente
  escribe «9/9/79»: se pregunta de nuevo. No hay bucle (la respuesta la toma `IntakeStep`, sin
  modelo). No está medido (EV-11).
- Lo que sigue a un dato retenido también se pregunta: `Intake.Start` corta en el primer hueco
  (`AgentIntake.cs:23`). Es el comportamiento que ya fija `AKnownAnswerThatDoesNotFitIsAskedNotKept`.
- Trampa del regex de teléfono: «…1990. 7000 0001» puede leerse como una sola corrida de dígitos.
  El regex no debe cruzar puntuación seguida de espacio; EV-4(d) y los montajes ponen el teléfono
  tras una coma.
- `mujer` en «mi mujer Ana» se toma como sexo femenino del paciente. Hoy solo lo detiene la
  tarjeta. Es el motivo de A-2.
- Pisar trabajo ajeno: en el checkout compartido el WIP de otra sesión compila con lo tuyo y
  `bin/obj` se corrompe con dos corridas simultáneas. El worktree del paso 0 evita ambos. Nunca
  `git add` en el checkout principal.

### Convivencia con `agent-jobs-health.md`

- Sin conflicto textual: este plan toca `AgentRuntime.cs` hasta `:363`; aquel toca `AgentWorker`,
  de `:616` en adelante.
- **Orden recomendado: jobs-health primero.** Sus cambios quedan debajo y no mueven ninguna línea
  citada aquí.
- No correr `test-backend.sh` a la vez en el mismo directorio. Ninguno toca `AgentHarness`.

## 6. EV-8 (opcional, no implementar hasta que el propietario confirme A-2)

- Cambio mínimo: el campo 3 siempre nulo en el filtro, y se borra `SexOf` de `Wrote`.
- La spec no lo resuelve del todo. A-2 dice «cuesta un toque», pero como el formulario corta en el
  primer hueco, también se vuelven a preguntar los tres datos del contacto. Evitarlo exige que
  `Intake.Start` y `Answer` salten al primer paso vacío; eso cambia el formulario y roza EV-10. El
  assert de EV-8 pasa con ambas variantes, así que no las distingue: hay que decidirlo con A-2.
- Asserts que cambiarían: `AgentHandsTheFormWhatItAlreadyKnows`, `ARelationshipSaid…`, los dos
  usuarios de `ProposeAndGetCode` y `:83`. Todos necesitan un turno más con el toque «f» o «m».

## 7. Lo que NO se hace

- Retirar `propose_registration` de `Tools()`: dejaría `InvalidRegistrationIsNeverProposed`,
  `LinkedPatientIsNeverRegisteredAgain` y unas 50 evals con `noTools` verdes en vacío.
- Devolver error al modelo cuando falta evidencia: el texto final del modelo («Revisa tus datos y
  confirma») llegaría al paciente sin tarjeta. Por eso la lambda cae al formulario.
- Poner el filtro dentro de `ProposeRegistration` o de `Intake.Start`: los llama el formulario sin
  modelo con respuestas tecleadas.
- Quitar las anclas de los regex de `Date`: el paso 3 empezaría a aceptar «nací el…».
- Tipo `Evidence`, parámetro nuevo al modelo, cambio de prompt, cambio de harness.
- Coincidencia por sufijo de teléfono («+503 70000007» frente a «7000-0007»): se pregunta.
- Correr evals.

## Asunciones del plan, a confirmar

- **P-1 (sexo sin EV-8).** EV-4 no dice cómo se compara el sexo y EV-3 exige tomarlo si consta. Se
  usa el vocabulario del paso 4 (`AgentIntake.cs:43`), incluidos «f» y «m», que son el toque del
  botón. Desaparece si se confirma A-2.
- **P-2 (nombres).** «Palabra completa» se lee como palabra por palabra en cualquier mensaje de la
  conversación, no como frase contigua. El modelo puede recombinar palabras escritas; lo detiene la
  tarjeta, como admite «Límites conocidos».
