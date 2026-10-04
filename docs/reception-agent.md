# Agente de atención: alcance, guardas y evals

Petición del propietario (2026-10-03): el agente de WhatsApp atiende citas, recetas emitidas e información habitual del hospital; sólo actúa sobre trabajo de recepción; y hay evals que dicen si funciona. El runtime usa Microsoft Agent Framework (`Microsoft.Agents.AI`). Amplía `appointment-agent.md`; no cambia sus decisiones.

## Lenguaje

El paciente escribe; el agente **responde**, **consulta** (agenda, recetas emitidas, guía), **propone** un cambio de cita, **entrega** una receta ya firmada o **deriva** a una persona. Sólo el paciente **confirma**. Una **guarda** es código que decide sin consultar al modelo; una **eval** es un caso sintético que corre el runtime real contra el modelo real.

## Invariantes

- INV-1: el agente no diagnostica, prescribe, ajusta dosis ni interpreta síntomas; esas consultas se derivan.
- INV-2: toda dosis o frecuencia que el agente escribe consta literalmente en una receta emitida leída en este turno, en la guía del hospital o en un mensaje previo del equipo.
- INV-3: ninguna escritura en la agenda ocurre sin `CONFIRMAR <código>` del paciente; el modelo nunca afirma que una propuesta ya se ejecutó.
- INV-4: identidad, teléfono y tenant los fija el servidor; ningún argumento del modelo los cambia.
- INV-5: una respuesta retenida por una guarda no llega al paciente: la conversación pasa a una persona y queda registro sin texto clínico.

## Criterios

| N.º | Criterio | Cómo se prueba |
|---|---|---|
| 1 | El turno del modelo corre en un `ChatClientAgent` de Agent Framework con las herramientas existentes; comportamiento previo intacto (pausas, coalescencia, idempotencia, confirmación). | `AgentIntegrationTests` existentes sin modificar, sobre el runtime nuevo. |
| 2 | Guarda de dosis (INV-2): una respuesta con dosis/frecuencia no respaldada se retiene y deriva. La misma dosis leída con `get_prescription` en el turno sí se envía. | `AgentGuardTests.UngroundedReplyIsWithheld…` y `GroundedPrescriptionInstructionsAreSent` con modelo simulado. |
| 3 | Guarda de enlaces: una URL que no está en la guía ni en resultados de herramientas retiene la respuesta. | `AgentGuardTests`, caso enlace. |
| 4 | Guarda de confirmación (INV-3): con una propuesta creada en el turno, afirmar que la cita quedó agendada/cancelada retiene la respuesta; si la respuesta omite el código, el runtime añade la instrucción `CONFIRMAR <código>`. Una sola propuesta por turno. | `AgentGuardTests`, casos confirmación falsa, código añadido y segunda propuesta rechazada. |
| 5 | Guarda de instrucciones: la respuesta no reproduce el prompt del sistema. | `AgentGuardTests`, caso fuga de prompt. |
| 6 | Presupuesto: como máximo 12 llamadas de herramienta por turno; al excederlo se deriva. | `AgentGuardTests.ToolBudgetHandsOff`. |
| 7 | Cada retención registra una actividad `guard` con la categoría, sin el texto retenido. | Asserts de actividad en los casos 2–5. |
| 8 | Evals: casos sintéticos por área (citas, recetas, información, alcance, seguridad, urgencia, registro) ejecutan `AgentRuntime` real contra el modelo configurado, con Hospital y WhatsApp simulados; se califican con `LocalEvaluator` de Agent Framework (herramientas usadas/prohibidas, derivación, documento entregado, texto exigido/prohibido, cero escrituras en Hospital). | `scripts/eval-agent.sh`; salida por caso. |
| 9 | Los casos son válidos sin modelo: id único, `synthetic: true`, `danger` escrito y las siete áreas cubiertas. | `AgentEvalCasesAreValid` en la suite normal. |
| 10 | Información habitual (horarios, ubicación, pagos, seguros, preparación): se responde sólo desde la guía del hospital; si el dato no está, el agente lo dice y ofrece recepción, sin inventar. | Evals del área `info`. |
| 11 | Fuera de alcance (tareas ajenas, otros pacientes, revelar instrucciones): el agente declina y redirige a lo que sí atiende, sin usar herramientas de paciente. | Evals de las áreas `alcance` y `seguridad`. |
| 12 | Proveedores en orden: NVIDIA NIM primero; si la llamada al modelo falla por transporte, 5xx, 429 o tiempo, se repite esa misma llamada en OpenAI API cuando `OPENAI_API_KEY` está configurada. Sin clave de OpenAI el fallo deriva como hoy. Nunca se repite una herramienta. | `AgentGuardTests.ModelFailureFallsBackToOpenAi…` (NIM 503 → OpenAI responde, una sola ejecución de herramienta) y `…WithoutFallbackKeyHandsOff`. |
| 13 | El modelo NIM por defecto es el que mejor califica en las evals entre los candidatos del catálogo; `AI_MODEL` lo sigue pudiendo fijar. | Tabla de resultados por modelo en «Verificación». |
| 14 | El modelo recibe la fecha, hora y día de la semana locales del hospital, y los horarios libres ya etiquetados con fecha, día y hora locales; no hace aritmética de calendario. | Evals `urgencia-003` y `citas-001/002`: de noche, «mañana» y el día de la semana ofrecidos coinciden con el calendario del hospital. |
| 15 | Sólo el mensaje final del modelo llega al paciente, sin Markdown. | `AgentGuardTests.OnlyTheClosingMessageReachesThePatient`; evals con `\*\*` prohibido. |
| 16 | Cada derivación entrega al paciente el teléfono de urgencias que el hospital configuró (Configuración → Teléfono de urgencias); sin configurar, el aviso genérico. | `AgentIntakeTests.HandoffGivesTheHospitalsEmergencyNumber`; eval `urgencia-001`. |
| 17 | Un contacto sin expediente se registra por WhatsApp con el alta de pacientes de Hospital (`POST v1/patients`): nombres, apellidos, fecha de nacimiento, sexo registral y un contacto de emergencia; el teléfono es el de la conversación, nunca un argumento. Nada llega a Hospital antes de `CONFIRMAR <código>`. | `AgentIntakeTests.NewClientIsRegisteredOnlyAfterConfirmingAndCanThenBook`; evals `registro-001`, `registro-003`, `urgencia-004`. |
| 18 | Un posible duplicado nunca se fuerza: la conversación pasa a recepción y el contacto queda sin vincular. | `AgentIntakeTests.PossibleDuplicateIsNeverForcedAndGoesToReception`. |
| 19 | No se registran menores de 18 años, terceros, ni datos incompletos o inválidos; un contacto ya vinculado no se registra de nuevo. La propuesta confirmada no conserva datos personales. | `AgentIntakeTests.InvalidRegistrationIsNeverProposed`, `LinkedPatientIsNeverRegisteredAgain`; evals `registro-002`, `registro-004`. |
| 20 | Toda propuesta lleva una línea escrita por el servidor con lo que se ejecutará (fecha local exacta de la cita, o los datos del registro); una cita exige inicio futuro con zona. | `AgentGuardTests.ProposalAlwaysStatesTheExactLocalDateTheServerWillExecute`, `AppointmentProposalNeedsARealFutureStart`. |
| 21 | La consulta de horarios responde con la fecha pedida o, si no tiene horarios libres, con el primer día siguiente que sí (hasta 14 días); los horarios tomados no se ofrecen. | `AgentGuardTests.AvailabilityAnswersWithTheFirstDayThatHasFreeSlots`; eval `urgencia-003`. |
| 23 | La primera cita de un cliente registrado en la conversación se crea como `first-visit`; las demás siguen como `follow-up`. | `AgentIntakeTests.FirstAppointmentOfARegisteredClientIsAFirstVisit`. |
| 22 | «Contacto de emergencia» es un dato del registro, no una urgencia; el resto de la lista de urgencias no cambia. | `AgentGuardTests.EmergencyContactIsRegistrationDataNotAnEmergency`. |

## Anti-criterios

Guardas que dependan de que el modelo obedezca; relajar una eval para que pase; evals que se salten en verde cuando falta la clave; datos reales en un caso; enviar WhatsApp o escribir en Hospital durante una eval; que el agente cotice precios que la guía no trae; reintentar una respuesta retenida.

## Preguntas

- Asumida: los precios se responden sólo si están en la guía (la guía sembrada dice «no confirmar precios sin consultar recepción»). Una herramienta de catálogo comercial no entra en este cambio.
- Asumida: la lista de urgencias que deriva sin modelo no cambia (emergencia, sobredosis, suicidio, pedir doctor o persona). Ampliarla es decisión clínica.
- Asumida: una dosis mencionada sólo por el paciente no respalda la respuesta; repetirla deriva. Falso positivo aceptado por seguridad.
- Bloqueantes: ninguna.

## Plan

1. Rojo: `AgentGuardTests` contra el runtime actual (envía dosis inventadas, enlaces y confirmaciones falsas).
2. `AgentGuard` (reglas puras) y `AgentRuntime` sobre `ChatClientAgent` con middleware de herramientas y de respuesta.
3. Casos en `evals/agent/cases/`, arnés `AgentEvals` y `scripts/eval-agent.sh`; `test-backend.sh` excluye la categoría `Eval`.
4. Suite completa y evals contra NIM con datos sintéticos.

No se hace: catálogo de precios, clasificador con segundo modelo, cambios en Hospital, activar el agente en ningún hospital.

## Verificación · 2026-10-03

- Rojos previos, pegados en la sesión: el runtime anterior enviaba dosis inventadas, enlaces inventados y «tu cita ya fue cancelada» sin confirmación (8 fallos); de noche ofrecía «mañana» corrido un día por usar la fecha UTC; no había registro ni teléfono de urgencias (3 fallos); la frase «contacto de emergencia» disparaba la derivación de urgencias y una propuesta podía decir «mañana» para un lunes (5 fallos).
- `scripts/test-backend.sh`: 215 pasan / 0 fallan / 0 omitidas, más la prueba de primer horario libre añadida después (corrida sola: 1/1). El conteo incluye pruebas de otra sesión que trabaja en este árbol; las pruebas existentes del agente no se modificaron.
- `scripts/eval-agent.sh`, 29 casos en 7 áreas, `nvidia/nemotron-3-super-120b-a12b`: 29 pasan. Corrida después de las 18:00 hora del hospital, que es cuando el desfase UTC existe.
- Frontend: `typecheck`, `lint` y formato limpios tras añadir el campo de teléfono de urgencias.
- Modelos NIM comparados con los primeros 21 casos, una corrida cada uno:

| Modelo | Pasan | Tiempo | Nota |
|---|---|---|---|
| `nvidia/nemotron-3-super-120b-a12b` | 21/21 | 70 s | elegido: mejor resultado y el más rápido |
| `z-ai/glm-5.3` | 21/21 | 328 s | igual resultado, casi cinco veces más lento |
| `nvidia/nemotron-3-ultra-550b-a55b` | 20/21 | 115 s | no derivó a un contacto sin expediente |
| `moonshotai/kimi-k3` | 11/21 | 145 s | caídas por 429 (límite de la cuenta), no por calidad |
| `deepseek-ai/deepseek-v4.1-flash` | — | — | interrumpido: cada llamada agotaba 100 s |

- Defectos que las evals encontraron y quedaron corregidos: fechas corridas de noche; UUID interno de la receta mostrado al paciente; negritas Markdown; «contacto de emergencia» tratado como urgencia; propuesta con «mañana» para otro día.
- OpenAI como respaldo: probado sólo con proveedor simulado. No hay `OPENAI_API_KEY` configurada.
- Recorrido real `scripts/live-agent.sh` (agente y modelo reales contra el Hospital local, clínica sintética; WhatsApp simulado): 1 pasa. Un cliente nuevo se registra, confirma, agenda el primer horario libre (lunes 5 de octubre de 2026, 08:00) y la cita se lee de vuelta `booked` en la agenda de Hospital; responde el horario de sábados, consulta su cita y, ante «cita de emergencia», deriva con el teléfono de urgencias. Cada corrida deja un paciente y una cita sintéticos en Hospital.
- Cifras finales: `scripts/test-backend.sh` 217 pasan / 0 fallan; `scripts/eval-agent.sh` 29/29; frontend `test:server` 12/12.
- No ejecutado: navegador (Playwright) y `tests/api_smoke.py`, que necesitan la contraseña de Keycloak de desarrollo; ni un mensaje real de WhatsApp.

## Límites conocidos

- Urgencia: quien escribe «emergencia» o describe síntomas se deriva a una persona con el teléfono de urgencias; el agente no agenda ni valora. Si nadie atiende la bandeja de noche, la conversación espera.
- Un paciente que ya existe en Hospital pero cuyo contacto no está vinculado no se autovincula: si intenta registrarse, Hospital detecta el posible duplicado y recepción lo vincula.
- El registro por WhatsApp no crea el cliente comercial de Hospital; lo hace el vínculo manual o la siguiente sincronización.
- Una propuesta de registro no confirmada conserva los datos del paciente en el historial del CRM hasta que alguien la depure; la confirmada los borra.
- Una dosis escrita con palabras («dos tabletas») no la detecta la guarda de dosis; la cubren el prompt y las evals.
- El reloj del runtime no es inyectable: los casos nocturnos sólo ejercitan el desfase UTC si se corren después de las 18:00 hora del hospital.
