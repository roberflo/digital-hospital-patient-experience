# La hora antes que el registro (S3)

Petición del propietario (2026-10-04): «…en especial crear el cliente antes de la cita.»
Lectura: lo que molesta es tener que registrarse antes de ver una hora. **Confirmada por el
propietario el 2026-10-04 (B-2 = opción A).** Enmienda `reception-agent.md` criterios 26 y 32; conserva 17–19, 23, 35 y 36 para quien
se registra sin haber elegido hora.

## Lo que ya existe

- Lista tocable, propuesta y confirmación de cita para quien tiene expediente
  (`AgentRuntime.cs:296-302, 427-440, 532-566, 584-622`).
- Horario pasado o mal formado no se propone (`:300`); ocupado al confirmar no se agenda
  (`:602`); fallo de agenda pasa a una persona sin reintento (`:621`); duplicado no se fuerza
  (`:628`). Ninguna prueba afirma los textos de `:602`, `:621` ni `:298`.
- Lo que lo impide sin expediente: `:98` (formulario directo), `:417` (lista vacía), `:298`
  (toque rechazado), `:534` y `:590` (`RequirePatient`).
- Hospital exige paciente existente para la cita y los siete datos para el paciente
  (`HospitalClient.Agent.cs:14-19`); no existe reserva provisional.

## Lenguaje

**Hora elegida**: intención guardada en Recepción como estado de trabajo; no es una cita ni una
reserva en Hospital. **Tarjeta única**: una propuesta con los datos del registro y la cita.

## Invariantes

- INV-T-1: antes de Confirmar, lo único que se pide a Hospital es leer la agenda.
- INV-T-2: la hora elegida nunca se le presenta al paciente como apartada, reservada o agendada
  antes de Confirmar.
- INV-T-3: tras Confirmar, la cita solo se crea si el paciente quedó creado, verificado y
  vinculado en ese mismo turno.
- INV-T-4: ningún POST a Hospital se repite.
- INV-T-5: la hora elegida desaparece con el formulario y no queda en el historial.

## Criterios

Todas por `AgentRuntime.Run` con `AgentHarness`, `NoModel` y un Hospital simulado que registra
ruta y orden de cada petición.

| N.º | Criterio | Cómo se prueba |
|---|---|---|
| T-1 | Sin expediente, «Agendar cita» devuelve los primeros horarios como lista, sin modelo y sin abrir el formulario. Sin horarios o con Hospital caído: oferta de persona, como hoy para quien tiene expediente. | `MenuForSomeoneWithoutRecordStartsTheRegistrationForm` se reemplaza (enmienda declarada del criterio 32): `h.Interactive[^1].type == "list"`, cero actividades `intake`, `model.Calls == 0`. Ejercita `:98` y `OfferSlots`. |
| T-2 | Por la vía del modelo, `hospital_availability` también llena la lista para quien no tiene expediente. | `ContactWithoutRecordGetsNoTappableSlots` se invierte y renombra (enmienda del criterio 26): `Assert.Single(h.Interactive)`. Ejercita `Availability` (`:417`). |
| T-3 | Tocar un horario sin expediente guarda la hora elegida y abre el formulario con «Para apartar el *<fecha y hora>* necesito registrarte: son 7 datos cortos.» No hay propuesta. | Nueva: `Say("CITA …")`. Assert: una actividad `intake`, cero `proposal:*`, cero escrituras en Hospital. Ejercita `ProposeTapped` (`:298`). |
| T-4 | Un horario pasado o mal formado tocado sin expediente no abre nada: «ya no está disponible». | `StaleOrMalformedSlotIsNeverProposed` con una variante sin `h.Link()`: cero `intake`, cero `proposal:*`. |
| T-5 | Al completar el formulario llega una sola tarjeta: datos, `*Cita:*`, `*Con:*` y «¿Confirmo tu registro y tu cita?», con Confirmar / Corregir datos / Otro horario. Una sola `proposal:*`. | Nueva: T-3 más siete respuestas. Assert sobre `h.Sent[^1]`, `ReadsLikeAChat`, tres botones, `Assert.Single` de `proposal:*`, cero escrituras. Ejercita `IntakeStep` (`:468-478`). |
| T-6 | Confirmar ejecuta, en orden: comprobar horario, `POST /v1/patients`, verificar y vincular, `POST` de la cita como `first-visit` para ese paciente. El paciente lee que quedó registrado y agendado, con «Recordarme la cita». | Nueva: lista ordenada de peticiones; `visitKind == "first-visit"` y `patientId` igual al creado. Ejercita `Confirm` (`:584`). |
| T-7 | Horario ocupado al confirmar: el paciente se registra, no se crea cita, y recibe «Ya te registré. Ese horario se acaba de ocupar.» con la lista nueva. | Nueva: el Hospital simulado devuelve el horario con `takenBy = 1` en la comprobación. Un POST de paciente, cero de cita, `PatientId` no nulo, lista en `h.Interactive[^1]`. |
| T-8 | Posible duplicado: no se fuerza, no se vincula, no se crea cita; se ofrece una persona y el motivo para el equipo nombra la hora que quería. | Variante de `PossibleDuplicateIsNeverForcedAndReceptionIsOffered`: cero POST de cita; el `handoff_offer` contiene fecha y hora. |
| T-9 | Paciente creado y la cita falla: queda vinculado y con `patient_registered`; la conversación pasa a una persona; nada se reintenta; ningún mensaje dice que la cita quedó agendada. | Nueva: el POST de cita responde 500. `Status == "human"`, un POST de cada tipo, ningún `h.Sent` con «quedó agendada». Ejercita `:621`. |
| T-10 | Menor detectado tras elegir hora: oferta de recepción, formulario y hora eliminados, cero escrituras. | Variante de `AMinorInTheFormIsOfferedReception` que empieza tocando un horario. |
| T-11 | Abandono: «salir», una pregunta, o 30 minutos sin respuesta eliminan la hora elegida; ninguna actividad la conserva; nunca hubo petición de reserva (INV-T-1, INV-T-5). | Nueva: T-3, respuesta «salir». Cero `intake`; ningún `Activity.Body` contiene el `startsAt`; el Hospital simulado solo vio `GET booking-options`. |
| T-12 | Ningún mensaje anterior a Confirmar afirma que la hora está tomada (INV-T-2). | Ningún `h.Sent` casa con `(?i)\b(apart|reserv|agend)(ad[ao]s?|é|amos|aste)\b` (cubre «te aparté», «reservé»; no casa con el infinitivo «Para apartar el…»), en el recorrido de T-5, la lista de T-1, «Corregir datos», la lista de «Otro horario» y la tarjeta reemitida. |
| T-13 | «Corregir datos» reinicia el formulario conservando la hora; «Otro horario» muestra la lista y, al tocar, reemite la tarjeta con los mismos datos. Ambos sin modelo; la propuesta anterior deja de poder confirmarse. | Nueva, dos casos, `NoModel`. Tras cada uno, `CONFIRMAR <código viejo>` responde «ya venció o no existe». |
| T-14 | Quien pide registrarse sin haber elegido hora sigue el camino actual: tarjeta de registro, Confirmar, lista. | Sin modificar y en verde: `AgentHandsTheFormWhatItAlreadyKnows`, `NewClientIsRegisteredOnlyAfterConfirmingAndCanThenBook`, `FirstAppointmentOfARegisteredClientIsAFirstVisit`. |
| T-16 | Un «sí» escrito solo confirma una propuesta cuando la tarjeta es lo último que dijo el agente. Tras «Otro horario» (o cualquier otro mensaje del agente después de la tarjeta), «sí» responde a ese mensaje y no confirma nada. Vale con y sin expediente. Añadido tras la revisión adversaria: sin esto, «Sí, el viernes» después de la lista creaba el paciente y la cita en la hora rechazada. | `YesAfterAskingForAnotherHourConfirmsNothing` (teoría), `YesAfterAskingForAnotherHourConfirmsNothingWithARecord`: cero POST, la propuesta sigue viva, el paciente recibe respuesta. `SayingYesToTheProposalJustMadeConfirmsIt` sin tocar. |
| T-17 | Un toque de horario sin expediente con doctor vacío o duración fuera de 5–480 minutos no abre nada; una propuesta que Hospital rechazaría al confirmar pasa a una persona, no falla en silencio. | Dos `InlineData` en `AStaleOrMalformedHourOpensNothingWithoutARecord`; `ACardHospitalWouldRefuseGoesToAPersonAtConfirming`. |
| T-18 | Paciente creado y la verificación falla: no se crea cita, pasa a una persona y el aviso al equipo lleva la referencia del expediente que devolvió Hospital; esa referencia nunca llega al paciente. | `WhenTheNewRecordCannotBeVerifiedNothingIsBookedAndTheTeamReadsWhichRecord`. |
| T-19 | Un comando del menú (`AGENDAR`, etc.) recibido a mitad del formulario lo cierra y se atiende como comando; no se toma como respuesta. | `AMenuCommandInTheMiddleOfTheFormClosesItAndIsServed`. |
| T-15 | El prompt indica al modelo consultar horarios antes de registrar a quien pide cita. | **Hueco declarado:** solo observable con evals, que no son puerta. La vía del menú (T-1…T-13) no depende del modelo. |

## Anti-criterios

- Crear en Hospital un paciente, una cita o cualquier reserva antes de Confirmar.
- Decir «te aparté», «reservé» o equivalente antes de Confirmar.
- Reintentar el POST de paciente o de cita.
- Crear la cita si el paciente no quedó creado y verificado en ese turno.
- Forzar un duplicado o vincular solo un expediente existente.
- Guardar la hora elegida donde aparezca en `/api/activities` o sobreviva al formulario.
- Dos tarjetas o dos Confirmar para lo que es una sola decisión.
- Servir toques de quien no tiene expediente mientras la conversación espera a una persona
  (`AgentRuntime.cs:53`): se queda como está.
- Debilitar `StaleOrMalformedSlotIsNeverProposed` o `PossibleDuplicateIsNeverForcedAndReceptionIsOffered`.

## Fuera de alcance (por nombre)

Reserva provisional en Hospital · registrar a un tercero o a un menor · reducir preguntas (B-3)
· tarjeta de contacto de WhatsApp · `first-visit` repetido (`:606`) · cliente comercial de
Hospital · cualquier cambio en Hospital.

## Preguntas

**Resuelta**

- B-2 — ¿La persona elige hora antes de registrarse? **Sí (opción A), decidido por el propietario
  el 2026-10-04**: hora → datos → una tarjeta y un Confirmar. Descartadas: (B) registrarse primero
  con un aviso de disponibilidad en el primer mensaje; (C) dejarlo como está.

**Abiertas con el propietario (no bloquean esta entrega)**

- OQ-T-1 — **Tercero en dos mensajes.** La guarda «la petición es para otra persona» solo mira el
  último mensaje. «Una cita para mi papá» seguido de «a las 9 está bien» recibe la lista, y el
  formulario registraría al padre con el teléfono de quien escribe; el botón «Agendar cita»
  tampoco lo filtra. Ya existía por `start_registration`; esta entrega lo deja más a la mano
  porque quien no tiene expediente ahora ve la lista. Decidir si la guarda debe recordar la
  petición durante la conversación (y cómo se sale de ella: «entonces para mí»).
- OQ-T-2 — **Una tarjeta sin confirmar conserva datos y hora** en `proposal:*`, que
  `/api/activities` sirve sin redactar y nadie depura. Los datos personales ya quedaban así; la
  hora es nueva. El anti-criterio «no guardar la hora donde aparezca en `/api/activities`» se lee
  como referido al estado de trabajo (`intake`); para la propuesta, decide el propietario si debe
  borrarse al vencer.
- OQ-T-3 — Textos de borde imprecisos: una hora que ya pasó se anuncia como «se acaba de ocupar»;
  si la hora vence durante el formulario la tarjeta sale sin cita y sin avisar; ante un posible
  duplicado no se dice que la cita no se hizo.

**Asumidas**

- A-1: si el horario se ocupó, el paciente igual se registra (T-7): confirmó sus datos y así no
  los repite.
- A-2: el motivo de una oferta de persona puede nombrar la hora deseada (T-8); el historial ya
  guarda fechas de cita.
- A-3: la hora elegida vence con el formulario (30 min sin respuesta); la tarjeta, a los 15 min.
- A-4: tres botones en la tarjeta única; si T-13 resulta caro, `arquitecto` lo dice y se decide.
- A-5: depende de S1 para los textos y se beneficia de S2; no las bloquea.
