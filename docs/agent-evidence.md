# Evidencia, no confianza — registro por WhatsApp (S1)

Petición del propietario (2026-10-04): aplicar al agente el principio «evidencia, no confianza»
de `trycompai/crm` (`docs/agent.md`). Este documento cubre el primer corte: los datos de registro
que el modelo pasa a `start_registration` y `propose_registration`. Amplía `reception-agent.md`;
no cambia sus decisiones.

Anclaje: árbol local de Recepción al 2026-10-04 (`main` 8 commits delante de `origin/main`).
`start_registration` no existe en `origin/main`; `propose_registration` sí.

## Lo que ya existe

- Ninguna herramienta del agente acepta confianza, puntaje ni URL (`AgentRuntime.cs:320-364`).
- El formulario sin modelo ya cumple el principio: «sólo lee lo que se escribió»
  (`AgentIntake.cs:4-5`).
- Nada llega a Hospital sin confirmación del paciente (INV-3; `AgentRuntime.cs:549-600`).
- Identidad cerrada: teléfono exacto (`HospitalClient.cs:20-28`); posible duplicado pasa a
  recepción (`AgentRuntime.cs:593`); `forceCreateDespiteDuplicate = false`
  (`HospitalClient.Agent.cs:18`).
- El hueco: por la vía del modelo, los siete datos son lo que el modelo afirma. Sólo se valida
  su forma. `AgentIntakeFlowTests.AgentHandsTheFormWhatItAlreadyKnows` lo fija en verde.

## Lenguaje

- **Dato de registro**: nombres, apellidos, fecha de nacimiento, sexo registral, y nombre,
  parentesco y teléfono del contacto de emergencia.
- **Escrito por el paciente**: consta en un mensaje con `Sender == "patient"` de la conversación
  en curso (`AgentMemory.Session`).
- **Aportado por el modelo**: llegó como argumento de una herramienta.
- **Retener**: el dato no se toma, no se guarda y se le pregunta al paciente.

## Invariantes

- INV-EV-1: un dato de registro sólo entra en una propuesta si el paciente lo escribió o lo tocó
  en la conversación en curso. Lo que sólo afirma el modelo se pregunta.
- INV-EV-2: que una persona sea mayor de edad se decide con una fecha que ella escribió, nunca
  con una que aportó el modelo.
- INV-EV-3: si el paciente escribió dos valores distintos para el mismo dato, el dato se retiene
  entero. No se elige el último ni el que coincide con el modelo.
- INV-EV-4: ninguna herramienta del agente recibe una confianza, un puntaje ni una URL como
  prueba.
- INV-EV-5: un valor retenido no queda guardado en ninguna parte.

## Criterios

Todas las pruebas ejercitan `AgentRuntime.Run` real con `AgentHarness` (modelo simulado que
emite la llamada de herramienta, Hospital simulado), como `AgentIntakeFlowTests.cs:160-171`.
Los mensajes del paciente se siembran con `h.Say("patient", …)`.

| N.º | Criterio | Cómo se prueba |
|---|---|---|
| EV-1 | Ninguna herramienta ofrecida al modelo declara un parámetro de confianza, puntaje, certeza o URL de fuente. | Prueba nueva sobre el arreglo `tools` de la petición capturada al modelo simulado. Nace verde: su rojo se demuestra añadiendo temporalmente un parámetro `confidence` y pegando el fallo. **A confirmar por `arquitecto`:** que el harness captura `tools` y no sólo `messages` (`AgentMemoryTests.cs:18`). Si no, es hueco. |
| EV-2 | `start_registration` con un valor que el paciente no escribió: ese campo cuenta como no dado, el formulario lo pregunta, y no se crea ninguna actividad `proposal:*`. | Modelo emite `start_registration` con los siete datos; ningún mensaje del paciente los contiene. Assert: `h.Sent` contiene «¿Cuál es tu nombre?»; cero `proposal:*`. Rojo esperado hoy: sale la tarjeta. |
| EV-3 | Los valores que el paciente sí escribió se toman como hoy y no se vuelven a preguntar. | Mismo caso con los siete datos sembrados en mensajes del paciente. Assert: una tarjeta, una `proposal:*`. Es el assert actual de `AgentHandsTheFormWhatItAlreadyKnows`, con montaje nuevo. |
| EV-4 | La comparación es por naturaleza del dato, nunca aproximada: fecha como fecha leída día-primero por el mismo lector del formulario (`Intake.Date`); teléfono como dígitos (`Rules.Phone`); nombres y parentesco sin acentos ni mayúsculas, palabra completa. | (a) Paciente «9 de septiembre de 1979», modelo «09/09/1979»: se toma. (b) Paciente «03/04/1990», modelo «1990-03-04»: se pregunta la fecha. (c) Paciente «Marchetta», modelo «Marchetti»: se preguntan los apellidos. (d) Paciente «7000-0007», modelo «70000007»: se toma. |
| EV-5 | Una fecha aportada por el modelo y no escrita por el paciente no decide ni «adulto» ni «menor»: se pregunta. | (a) Sin fecha en los mensajes, modelo pasa una fecha adulta: cero `proposal:*`, `h.Sent` pide la fecha. (b) Paciente «01/01/2010», modelo «1990-01-01»: cero `proposal:*`; al responder «01/01/2010» sale `handoff_offer` de menor (camino `IntakeStep`, `AgentRuntime.cs:433-443`). |
| EV-6 | Dos fechas de nacimiento válidas y distintas escritas por el paciente en la conversación en curso: la fecha se retiene aunque el modelo aporte una de ellas. | Paciente «nací el 12/03/1990» y luego «perdón, 12/03/1991»; modelo pasa cualquiera. Assert: `h.Sent` pide la fecha; cero `proposal:*`. |
| EV-7 | Una llamada del modelo a `propose_registration` con algún valor no escrito por el paciente no crea propuesta ni tarjeta. | Modelo emite `propose_registration` sin mensajes que respalden los valores. Assert: cero `proposal:*`; ningún `h.Sent` con «Revisa tus datos»; cero `POST /v1/patients`. Vale tanto si se valida como si la herramienta deja de ofrecerse. Rojo esperado hoy: `AgentIntakeTests.cs:34-38` obtiene código. |
| EV-8 | El sexo registral nunca se toma del modelo: se pregunta con los dos botones (asunción A-2). | Paciente escribe «soy hombre, mi contacto es mi mujer Ana»; modelo pasa `sex`. Assert: `h.Sent` contiene «documento de identidad»; el JSON de la actividad `intake` no trae `Sex`. |
| EV-9 | Un valor retenido no se guarda: ni en `intake`, ni en `proposal:*`, ni en `agent_tool`, ni en ninguna otra actividad. | En EV-2 y EV-5(a): ningún `Activity.Body` de la conversación contiene el valor aportado. |
| EV-10 | El formulario sin modelo y la confirmación no cambian respecto a como quedan tras `agent-intake-voice.md` (textos nuevos, salto al dato faltante) y `agent-slot-first.md` (hora antes que el registro). | Sin modificar y en verde: las pruebas de `AgentIntakeFlowTests` que no usan modelo, y `AgentIntakeTests.PossibleDuplicateIsNeverForcedAndReceptionIsOffered`. |
| EV-11 | Las evals del área `registro` no empeoran. | **Hueco declarado:** `scripts/eval-agent.sh` necesita modelo real y comparte la clave de NIM con el agente en vivo (`reception-agent.md:154`). No es puerta de esta story; se corre cuando el propietario lo indique y se pega la salida por caso. |

### Pruebas existentes cuyo montaje cambia (sus asserts no)

Hoy pasan valores que el paciente nunca escribió; habrá que sembrar el mensaje del paciente:

- `AgentIntakeFlowTests`: `AgentHandsTheFormWhatItAlreadyKnows`,
  `AMinorIsNoticedAsSoonAsTheBirthDateIsKnown`,
  `ARelationshipSaidInFrontOfTheContactsNameIsNotAskedAgain`,
  `AModelThatSaysNothingAfterStartingTheFormStillStartsIt`.
- `AgentIntakeTests`: todo lo que pasa por el helper `ProposeAndGetCode` (`:34-38`) y las
  llamadas directas en `:83`, `:119`, `:130`.

Las que afirman tarjeta o paso tras tomar `sex` del modelo chocan con EV-8: su assert cambia sólo
si el propietario confirma A-2. Hasta entonces, EV-8 no se implementa.

## Límites conocidos

- La comprobación demuestra que el paciente escribió esas palabras, no que respondan a ese
  campo. Si el modelo pone el nombre del contacto como nombre del paciente, lo detiene la
  tarjeta, no el código.
- EV-6 retiene también cuando la segunda fecha no era de nacimiento («mi última consulta fue el
  05/01/2020»). Cuesta una pregunta de más; falso positivo aceptado.
- Lo dicho en días anteriores no cuenta (A-3): se pregunta otra vez.

## Anti-criterios

- Añadir al modelo un parámetro `kind`, `confidence`, `source` o similar: sería otra
  autoevaluación.
- Resolverlo en el prompt.
- Coincidencia aproximada de cualquier tipo (distancia de edición, prefijo, fonética).
- Rellenar un campo desde `contact.Name` (perfil de WhatsApp), `ContactMemories`, `recall` o
  conversaciones anteriores.
- Guardar el valor retenido «por si sirve».
- Verificar con un segundo modelo.
- Borrar o debilitar las pruebas que hoy usan `propose_registration` en vez de cambiar su
  montaje.
- Vincular solo un expediente existente, o enviar `forceCreateDespiteDuplicate = true`.
- Escribir cualquier dato clínico, o cualquier cosa en Hospital distinta del `POST v1/patients`
  actual.
- Correr evals con la clave de NIM de producción mientras el agente atiende.

## Fuera de alcance (por nombre)

`remember` y `recall` (S2) · `record_note` (S3) · `propose_action` y `my_appointments` (S4) ·
texto del paciente hacia el proveedor del modelo (S5) · `Confidence` en el recap de Hospital y
AI-1 (S6) · duplicados de pacientes en Hospital · `/api/assistant` del personal
(`AssistantEndpoints.cs`) · procedencia de `Contact.Name` · pedir DUI por WhatsApp · bandeja de
sugerencias para recepción (no existe y no se crea) · cualquier cambio en el repo Hospital.

## Preguntas

**Bloqueante**

- OQ-1 — Qué evidencia basta como fuente primaria de identidad para crear un expediente por
  WhatsApp. La contesta el propietario con el médico del piloto. **No bloquea EV-1…EV-10**,
  porque esta story sólo estrecha lo aceptado. Bloquea cualquier cambio que amplíe o sustituya
  la fuente. **Hasta que se conteste, se queda como está**: autodeclaración escrita desde el
  número de la conversación más confirmación de la tarjeta (criterios 17 y 19 de
  `reception-agent.md`).

**Asumidas**

- A-1: el tipo de evidencia lo deriva el servidor, no lo declara el modelo. Hoy sólo hay una
  fuente posible para un dato de registro (lo que el paciente escribió o tocó), y el código
  puede comprobarla directamente.
- A-2: el sexo registral siempre se pregunta con botones en la vía del modelo. «Mujer» u
  «hombre» en el chat no dicen de quién es el sexo. Cuesta un toque. **Pendiente de confirmar
  por el propietario; EV-8 no se implementa hasta entonces.**
- A-3: la fuente es la conversación en curso (12 h, 24 mensajes).
- A-4: al confirmar el registro, el contacto del CRM sigue tomando el nombre confirmado
  (criterio 51; `AgentRuntime.cs:596`), aunque recepción hubiera tecleado otro. Quien escribe
  es la confirmación del paciente, no el modelo. No hay columna que distinga el origen de
  `Contact.Name`, así que «no sobrescribir a un humano» no es comprobable hoy para ese campo.
- A-5: no existe el nivel «débil, guardado pero oculto» del CRM. Lo retenido no se guarda.
- A-6: «proponer a un humano» es la tarjeta al paciente o la oferta de pasar con recepción que
  ya existen.
