# Rastro del agente en la conversación

**Estado (2026-11-14, S1 construida sobre `a811340`):** S1 **construida**. Verificados: criterios 1 (backend),
2, 11 (backend; 1613/1613 con `scripts/test-backend.sh`), 3, 4, 5, 6 (`test:ui` 9/9, `test:server` 48/48, typecheck y
lint limpios). **Sin verificar:** criterios 7–10 y la parte de navegador del 1 (`tests/inbox-ux.spec.ts`, pruebas
`AT-1`, `AT-5`, `AT-7`…`AT-10` escritas, no corridas: la API y la web de Recepción no estaban levantadas ni se
pudo levantarlas sin tocar el checkout compartido); el movimiento reducido del 10 sigue siendo revisión visual.
Defecto de INV-4 corregido (`agent-notice.ts` gobierna aviso e insignia). OQ-1/2/3 no bloquearon S1 (statu quo
congelado); OQ-5 bloquea sólo S3. Pendiente fuera de alcance: la insignia de la lista y `/overview` (OQ-11).

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
- La conversación se reabre por URL (`/?view=inbox&conversationId=`, MR-54; `tests/activity.spec.ts:137-153`).
- El aviso de atención y «Activar agente» viven en `frontend/src/components/chat-panel.tsx:347-389`;
  el motivo del traspaso del agente, en `:332-338` (MR-47).
- `/me` trae `tenant.agentEnabled` (`backend/CrmEndpoints.cs:11`); cada conversación trae su canal
  con `enabled` (`backend/WhatsAppEndpoints.cs:26`, `frontend/src/lib/api.ts:67-73`).
- `GET /conversations` publica por fila `handoff { reason, at }` y `agentBlock { code, reason }`
  (`backend/WhatsAppEndpoints.cs:107-111`); `agentBlock` incluye `channel` y `paused` (`SEND_ENABLED`).
- Títulos de `guard`, `agent_provider`, `handoff_offer`, `patient_registered`,
  `prescription_delivered` (`activity-workspace.tsx:102-107`); `intake` y `visit_kind` fuera del feed
  (`ActivityFeed.cs:45`); rastro `agent_tool` para lo que el agente hace sin modelo (`AgentRuntime.cs:340`).
- La tarjeta de un paso existe: `EventCard` (`activity-workspace.tsx:147`), sin exportar.
- El chat ya sondea `GET /activities?contactId=` cada 8 s para «Historial del cliente»
  (`chat-panel.tsx:93-97`): ruta sin redactar y por contacto. Es B-1, fuera de alcance; el rastro no
  la usa ni la toca.

## Superficie

- Sin endpoint nuevo: `GET /api/activity-feed?conversationId=<id>&page=<n>`.
- Pantalla: la conversación abierta en `/?view=inbox`, dentro del aviso de atención
  (`chat-panel.tsx:347-389`), como desplegable cerrado por defecto. Sin `?view=` nuevo.

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
| 1 | Veo sólo lo que pasó en esta conversación, aunque el paciente tenga otra por otro número (INV-1). | Backend, prueba nueva en `backend.Tests/AgentTraceTests.cs` (ya llama a `ActivityFeed.Read` con `conversationId` en `:67`): un contacto con dos conversaciones devuelve sólo las filas de la pedida; el id de una conversación de otro inquilino, `Total == 0`. Navegador (`tests/inbox-ux.spec.ts`): la petición a `**/api/crm/activity-feed?*` lleva `conversationId` y no lleva `contactId`. |
| 2 | Cada paso se lee en palabras de recepción, nunca «Herramienta: …» ni el nombre interno (INV-2). | Prueba nueva sobre `ActivityFeed.DisplayBody` en `AgentTraceTests.cs`: para cada herramienta registrada en `AgentRuntime.Tools.cs` (17 hoy) y para un nombre desconocido, la salida no contiene «Herramienta» ni el nombre. Rojo esperado hoy: `hospital_services`, `hospital_contact`, `hospital_disciplines`, `hospital_agreement` y el desconocido (`ActivityFeed.cs:33` devuelve el cuerpo crudo). |
| 3 | Si el agente pasó la conversación y nadie ha respondido, el aviso existente dice el motivo **y la hora**; con el rastro abierto, el paso de derivación (del agente, del sistema o externo) muestra quién, cuándo y el motivo. | `ui-tests/chat-panel.test.ts` (render real de `ChatPanel`, `npm run test:ui`): con `handoff: { reason, at }` el marcado contiene la hora en la zona del inquilino. Componente del rastro renderizado con `SWRConfig.fallback` y una fila `kind: handoff`, `careType: system`: actor, hora y cuerpo presentes. |
| 4 | Una respuesta retenida se lee como tal, con su categoría y la aclaración de que el texto no se guardó. | Mismo `ui-tests`, fila `guard` («Respuesta automática retenida: dosis.», `AgentRuntime.cs:274`): título «Retuvo una respuesta automática», la categoría y la frase aclaratoria. Que el servidor no guarda el texto ya lo afirma `backend.Tests/AgentGuardTests.cs:54`. |
| 5 | El aviso y la insignia de la cabecera sólo dicen que el agente atiende si es cierto; si no, dicen por qué (agente apagado para el hospital, número desactivado o envío automático en pausa) y puedo tomar la conversación (INV-4). | Función pura nueva en `frontend/src/lib/agent-notice.ts` sobre `status`, `me.tenant.agentEnabled`, `chat.agentBlock` y `me.role`, importada por `chat-panel.tsx`; `server-tests/agent-notice.test.ts` (`npm run test:server`, corre en CI) con todas las combinaciones. Rojo esperado hoy: `status: 'agent'` con `agentBlock: { code: 'channel' }` produce «El agente está atendiendo». `ui-tests/chat-panel.test.ts`: con ese fixture «Tomar conversación» no está deshabilitado. |
| 6 | Si el agente no hizo nada aquí, me lo dice en una frase, no con una lista vacía. | `ui-tests`: componente del rastro con `fallback` `items: []`. |
| 7 | Si el rastro no carga, veo el error con «Reintentar» y sigo pudiendo responder al paciente. | Navegador (`inbox-ux.spec.ts`): feed simulado con 500; «Mensaje al paciente» sigue habilitado (etiqueta verificada en `activity.spec.ts:148`). |
| 8 | Puedo llegar a los pasos anteriores cuando hay más de 30. | Navegador: `total: 31`, segunda página pedida con `page=2` (patrón `activity.spec.ts:116-136`). |
| 9 | Con el rastro abierto en la página 1, un paso nuevo aparece solo en 10 s o menos; cerrado, o en otra página, no se consulta. | Navegador: contar peticiones al feed simulado con el rastro cerrado (0) y abierto. |
| 10 | Lo uso con teclado y lector de pantalla, en 360 px sin desplazamiento horizontal y sin animación con movimiento reducido. | Navegador, proyectos `desktop` y `mobile` de `playwright.config.ts`: abrir por teclado, `aria-expanded`, `scrollWidth <= innerWidth` (patrón `activity.spec.ts:99`). El movimiento reducido es revisión visual: **hueco declarado**. |
| 11 | Abrir el rastro no deja huella en los datos (INV-3). | Backend (`AgentTraceTests.cs`): contar `Activities`, `Audits` y `Conversation.Revision` antes y después de `ActivityFeed.Read`. |

`test:server` corre en CI (`.github/workflows/verify.yml:26`); `test:ui` y Playwright **no**. Playwright
además inicia sesión en el Keycloak de Hospital y necesita `KC_DEV_USERS_PASSWORD`. Los criterios 3,
4 y 6 quedan sin verificar sin una corrida local de `test:ui`; los 7 a 10, sin una de Playwright.
No en verde: sin verificar.

## Anti-criterios

- Un endpoint, tabla o `?view=` nuevos.
- Alimentar el rastro desde `GET /api/activities` (sin redactar, `CrmEndpoints.cs:84`) o filtrar
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
- Usar el sondeo existente de `/activities?contactId=` (`chat-panel.tsx:93-97`) como fuente del rastro.
- Una segunda tarjeta de paso o un segundo mapa de títulos: se exportan `EventCard` y `titles`.
- Sondear el feed con el rastro cerrado: cada lectura descifra todos los contactos del inquilino
  (`ActivityFeed.cs:51`).
- Dejar un nombre de herramienta sin etiqueta «porque es nuevo»: lo desconocido cae en una frase genérica.

## Fuera de alcance, por nombre

S2 (constancia de lo que el agente descartó), S3 (el agente pregunta a recepción y continúa), S4
(Hospital, feature 09), B-1 (`/api/activities` sin redactar), la lista «Historial del cliente» de
la ficha (`chat-panel.tsx:252-266`, se queda como está), `/?view=activity`, el asistente del
equipo (`/api/assistant`), y una prueba para `crm-proxy.ts`.

La insignia «Agente IA» de la tarjeta de la lista y los conteos de `/overview` cuando `agentBlock` no
es nulo (MR-46 sólo cubre el agente apagado; mismo defecto, otra superficie), y añadir `test:ui` a
`verify.yml`.

## Preguntas

- **OQ-1 — no bloquea S1; bloqueante para S2, S3 y cualquier restricción (acceso a PHI: dueño +
  `security-auditor`).** ¿Quién lee el rastro y los mensajes? Statu quo congelado: todo miembro del
  inquilino (`ActivityFeed.cs:39`). S1 no amplía lectores: muestra, redactado y por conversación, lo
  que ese mismo miembro ya lee en `/?view=activity`.
- **OQ-2 — no bloquea S1; bloqueante para S2 (retención de PHI: dueño + `compliance-auditor`).**
  ¿Cuánto se conserva el rastro y qué más puede registrarse? Statu quo congelado: sin depuración de
  `Activities`; S1 no guarda nada nuevo.
- **OQ-3 — no bloquea S1; decide dueño + `security-auditor`.** ¿Abrir el rastro debe auditarse?
  Statu quo congelado: ninguna lectura de feed o mensajes se audita. S1 no añade auditoría de lectura
  (INV-3); si la respuesta es «sí», INV-3 y el criterio 11 se reescriben, y aplica también a
  `/?view=activity`.
- **OQ-4 — cerrada (2026-11-14).** P-1 está en `origin/main`.
- **OQ-5 — bloqueante para S3 (clínica y operativa: dueño + médico responsable).** ¿Puede el agente
  retomar con una respuesta de recepción y transmitirla al paciente? ¿Quién consta como autor?
  Hasta que se conteste, «preguntar a la persona» sigue siendo derivar.
- **OQ-6 — asumida.** El rastro va en el aviso de atención, desplegable y cerrado por defecto; no en
  pestaña ni en la ficha.
- **OQ-7 — asumida.** Se muestran los pasos de todos con su actor: el feed no distingue «relativo al
  agente» y `guard` es del sistema.
- **OQ-8 — asumida.** Sondeo de 10 s sólo abierto y en la página 1 (precedente: 15 s en
  `activity-workspace.tsx:280`).
- **OQ-9 — asumida.** Paginación «Anterior / Siguiente», la de `/?view=activity`; sin carga infinita.
- **OQ-10 — asumida.** Etiquetas nuevas: «Consulta del catálogo de servicios», «Consulta de dirección
  y teléfono», «Consulta de disciplinas», «Consulta de convenio»; nombre desconocido: «Acción
  automática». Texto de producto; el dueño puede cambiarlo sin tocar criterios.
- **OQ-11 — asumida.** El criterio 5 cubre el aviso y la insignia de la conversación abierta; la
  tarjeta de la lista y los conteos se quedan como están.
