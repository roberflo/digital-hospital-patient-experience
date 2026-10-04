# Rastro del agente en la conversación

Petición (2026-10-04): llevar a Recepción la «pestaña Agente» de `trycompai/crm`. Medido contra
`origin/main` (`6ff1cfd`), el agente de Recepción conversa con el paciente por WhatsApp, no con
recepción, así que no hay sesión en vivo ni puente que copiar. Esta spec es la diferencia que sí
aplica. Amplía `reception-agent.md`; no cambia sus decisiones.

## Propósito

Quien toma una conversación puede leer, sin salir de ella, qué hizo el agente ahí, por qué la pasó
a una persona, y si el agente de verdad está respondiendo.

## Lenguaje

- **Rastro**: las filas `Activity` de una conversación, de la más reciente a la más antigua.
- **Paso**: una fila del rastro, con quién la hizo (agente, sistema, persona) y cuándo.
- **Derivación**: el paso `handoff`; su texto es el **motivo**.
- **Retención**: el paso `guard`; guarda la categoría, nunca el texto retenido.
- **Atendiendo**: el servidor dejaría responder al agente en esta conversación ahora.

## Lo que ya existe (no se reconstruye)

- `GET /api/activity-feed` filtra por `conversationId`, redacta propuestas, pagina de 30 y exige
  sesión (`backend/ActivityFeed.cs:39-48,26-34,56`). El BFF ya lo deja pasar
  (`frontend/src/lib/crm-proxy.ts:13`).
- La conversación sobrevive a una recarga y se reabre por URL (`workspace.tsx:575-577`).
- Una conversación cerrada ofrece «Activar agente» (`workspace.tsx:1068-1081`).
- `/me` trae `tenant.agentEnabled` (`backend/CrmEndpoints.cs:11`); cada conversación trae su canal
  con `enabled` (`backend/WhatsAppEndpoints.cs:26`, `frontend/src/lib/api.ts:67-73`).
- En el árbol local sin publicar (`61b72c1` y cambios sin commit): títulos para `guard`,
  `agent_provider`, `handoff_offer`, `patient_registered`, `prescription_delivered`; el formulario
  en curso (`intake`) fuera del feed; rastro `agent_tool` para lo que el agente hace sin modelo.

## Superficie

- Sin endpoint nuevo: `GET /api/activity-feed?conversationId=<id>&page=<n>`.
- Pantalla: la conversación abierta en `/?view=inbox`, en el aviso de atención
  (`workspace.tsx:1063-1084`). Sin `?view=` nuevo.

## Forma y sitio del dato

No toca datos: sin tabla, columna ni migración. Lee `Activities` (cuerpo cifrado, `Models.cs:222`)
por la ruta existente.

## Qué la ata

- `reception-agent.md` INV-4 (identidad y tenant los fija el servidor) e INV-5 (retención sin texto
  clínico).
- `backlog.md` INV-1 (el tenant nunca viene del navegador) e INV-5 (acciones y resultados en el
  historial).
- `backend/ActivityFeed.cs:25`: el JSON de propuesta y los códigos no salen a un feed.
- Hallazgo: Recepción no tiene ADR; nada escrito decide quién lee el rastro ni cuánto se conserva
  (OQ-1, OQ-2).

## Invariantes

- INV-1: el rastro de una conversación sólo contiene filas con ese `ConversationId`; un id de otro
  inquilino devuelve cero filas, nunca 403.
- INV-2: ningún paso entrega JSON de propuesta, código de confirmación, formulario en curso, nombre
  de herramienta ni identificador interno.
- INV-3: abrir el rastro no escribe nada (ni `Activity`, ni `Audit`, ni `Revision`).
- INV-4: la pantalla no dice que el agente atiende cuando la atención automática del hospital está
  en pausa o el número está desactivado.
- INV-5: esta story no guarda ni muestra texto del modelo, razonamiento ni texto retenido.

## Criterios

| N.º | Criterio | Cómo se prueba |
|---|---|---|
| 1 | Veo sólo lo que pasó en esta conversación, aunque el paciente tenga otra por otro número (INV-1). | Backend, prueba nueva en `backend.Tests/AgentIntegrationTests.cs` llamando a `ActivityFeed.Read` con `conversationId` (hoy el helper `Feed` pasa `null`): un contacto con dos conversaciones devuelve sólo las filas de la pedida; el id de otro inquilino, `Total == 0`. Navegador: la petición lleva `conversationId` (patrón `frontend/tests/activity.spec.ts:108`). |
| 2 | Cada paso se lee en palabras de recepción («Consulta de disponibilidad: completado»), nunca «Herramienta: …» (INV-2). | Prueba nueva sobre `ActivityFeed.DisplayBody` (estática pública): para cada herramienta de `AgentRuntime.cs:230-244` y para un nombre desconocido, la salida no contiene «Herramienta» ni el nombre. Rojo esperado hoy: `send_latest_prescription` y `propose_registration`, ausentes en `ActivityFeed.cs:31`. |
| 3 | Si la conversación está con mi equipo, lo primero que leo es quién la pasó, cuándo y el motivo. | Función pura (estado + pasos → encabezado) en `frontend/src/lib/`, importada por el aviso; prueba en `frontend/server-tests/` con `npm run test:server`. La costura no existe: se crea. Navegador: feed simulado con un `handoff` de `careType: ai`. |
| 4 | Una respuesta retenida se lee como tal, con su categoría y la aclaración de que el texto no se guardó. | Misma función pura, con la fila `guard` («Respuesta automática retenida: dosis.», `AgentRuntime.cs:158`). El servidor ya afirma que el texto no se guarda (`backend.Tests/AgentGuardTests.cs:34`). Depende de P-1 para el título. |
| 5 | El aviso sólo dice «el agente está atendiendo» si es cierto; si no, dice por qué (atención automática en pausa para el hospital, o número desactivado) y puedo tomar la conversación (INV-4). | Función pura sobre `conversation.status`, `me.tenant.agentEnabled` y `chat.channel.enabled`; `test:server` con las combinaciones. Navegador: `/api/crm/me` simulado con `agentEnabled: false` y conversación en `agent`. **Hueco declarado**: la tercera condición de `AgentRuntime.cs:36` (`SEND_ENABLED`) sólo la ve un administrador (`CrmEndpoints.cs:80`); queda para S2. |
| 6 | Si el agente no hizo nada aquí, me lo dice en una frase, no con una lista vacía. | Navegador: feed simulado con `items: []`. |
| 7 | Si el rastro no carga, veo el error con «Reintentar» y sigo pudiendo responder al paciente. | Navegador: feed simulado con 500; «Mensaje al paciente» sigue habilitado (etiqueta verificada en `activity.spec.ts:148`). |
| 8 | Puedo llegar a los pasos anteriores cuando hay más de 30. | Navegador: `total: 31`, segunda página pedida con `page=2` (patrón `activity.spec.ts:116-136`). |
| 9 | Con el rastro abierto, un paso nuevo aparece solo en 10 s o menos; cerrado, no se consulta. | Navegador: contar peticiones al feed simulado con el rastro cerrado (0) y abierto. |
| 10 | Lo uso con teclado y lector de pantalla, en 360 px sin desplazamiento horizontal y sin animación con movimiento reducido. | Navegador, proyectos `desktop` y `mobile` de `playwright.config.ts`: abrir por teclado, `aria-expanded`, `scrollWidth <= innerWidth` (patrón `activity.spec.ts:99`). El movimiento reducido es revisión visual: **hueco declarado**. |
| 11 | Abrir el rastro no deja huella en los datos (INV-3). | Backend: contar `Activities` y `Audits` antes y después de `ActivityFeed.Read`. |

Las pruebas de navegador inician sesión en el Keycloak de Hospital y necesitan
`KC_DEV_USERS_PASSWORD`: no corren en CI (`.github/workflows/verify.yml`). Los criterios 6 a 10
dependen sólo de ellas; sin esa corrida quedan **sin verificar**, no en verde.

## Anti-criterios

- Un endpoint, tabla o `?view=` nuevos.
- Alimentar el rastro desde `GET /api/activities` (sin redactar, `CrmEndpoints.cs:64`) o filtrar
  por `contactId` (mezcla números).
- Consultar el feed sin `conversationId` en un sondeo: recorre y descifra el historial del hospital
  entero (`ActivityFeed.cs:54-67`).
- Mostrar nombres de herramienta, JSON, identificadores o códigos; redactar en el cliente lo que el
  servidor debió redactar.
- Guardar el texto retenido, el del modelo o su razonamiento «para explicar el porqué».
- Tomar el motivo de `Conversation.Summary` sin comprobar que se limpia al reanudar (no encontré
  que se limpie).
- Un cuadro para escribirle al agente (eso es S3).
- Token, puente o cabecera de registro: el contexto ya lo fija el servidor.
- Tocar `AgentRuntime.cs` (es S2, y otra sesión lo reescribe).
- Cambiar quién ve conversaciones o actividad.
- Añadir inglés o tema oscuro: Recepción no los tiene.
- Debilitar `ActivityKeepsHistoricalRolesRedactsProposalsAndJoinsLatestDelivery`.

## Fuera de alcance, por nombre

S2 (constancia de lo que el agente descartó), S3 (el agente pregunta a recepción y continúa), S4
(Hospital, feature 09), B-1 (`/api/activities` sin redactar), la lista «Historial del cliente» de
la ficha (`workspace.tsx:1279-1295`, se queda como está), `/?view=activity`, el asistente del
equipo (`/api/assistant`), y una prueba para `crm-proxy.ts`.

## Preguntas

- **OQ-1 — bloqueante (política, dueño del producto).** ¿Quién puede leer el rastro y los mensajes
  de una conversación: todo el equipo del hospital o sólo el responsable y los administradores?
  Statu quo congelado: todo miembro del inquilino (`WhatsAppEndpoints.cs:12-28`,
  `ActivityFeed.cs:39`). S1 no lo cambia; bloquea S2, S3 y cualquier restricción.
- **OQ-2 — bloqueante (política, dueño del producto).** ¿Cuánto se conserva el rastro y qué más
  puede registrarse (motivo de un rechazo, categoría de un fallo)? Statu quo congelado: no hay
  depuración y sólo se guarda lo de `AgentRuntime.cs:145,158,206,220,410,444`. S1 no guarda nada
  nuevo; bloquea S2.
- **OQ-3 — bloqueante (política, dueño del producto).** ¿Abrir el rastro debe auditarse? Statu quo
  congelado: ninguna lectura de feed o mensajes se audita; sólo las escrituras y la consulta
  clínica (`backend/ClinicalEndpoints.cs:45`). S1 no añade auditoría de lectura (INV-3).
- **OQ-4 — asumida.** S1 se construye cuando los 8 commits locales de `main` y los cambios sin
  commit de la otra sesión estén en `origin/main` (P-1). Si no van a publicarse, los criterios 2 y
  4 absorben sus títulos y la exclusión de `intake`.
- **OQ-5 — bloqueante para S3 (clínica y operativa, dueño del producto).** ¿Puede el agente
  retomar con una respuesta de recepción y transmitirla al paciente? ¿Quién consta como autor?
  Hasta que se conteste, «preguntar a la persona» sigue siendo derivar.
- **OQ-6 — asumida.** El rastro va en el aviso de atención de la conversación, desplegable, no en
  una pestaña ni en la ficha.
- **OQ-7 — asumida.** Se muestran los pasos de todos (agente, sistema, personas) con su actor: el
  feed no distingue «relativo al agente» y `guard` es del sistema.
