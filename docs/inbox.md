# Bandeja WhatsApp

La única bandeja de WhatsApp es **Bandeja de entrada** (`/?view=inbox`), servida por la API de
Recepción y aislada por hospital: conversaciones, mensajes, asignación, notas, etiquetas y
seguimiento viven en la base de Recepción, que los recibe por el webhook de Kapso
(`/webhooks/kapso` de la API). Las respuestas salen por `POST /api/conversations/{id}/messages`
(`ConversationService.Send`): pausan al agente, dejan la actividad y se deduplican por la
`Idempotency-Key` del cliente. Las compuertas `SEND_ENABLED` y `KAPSO_MANUAL_SEND_ENABLED` las lee
sólo la API.

La antigua vista `/inbox`, que leía Kapso directamente desde la web, se retiró el 2026-10-25 por
decisión del dueño (OQ-3 en [mejoras-revision-2026-10.md](mejoras-revision-2026-10.md)): `/inbox`
redirige a `/?view=inbox` y la web ya no guarda `KAPSO_API_KEY`, `KAPSO_PHONE_NUMBER_ID` ni
`KAPSO_WEBHOOK_SECRET` (son variables de la API).

## Filtro por número

El filtro «Filtrar por canal» (en «Más filtros») limita la lista a un número del hospital. Un
doctor con número propio —un canal cuyo doctor asignado es él— entra filtrado a ese número y puede
quitar el filtro con «Todos los canales» para ver las del hospital (MR-45). Un enlace con
`channelId` (p. ej. «Consultar historial» en `/whatsapp`), `conversationId` o `phone`, o la vista de
equipo de un responsable, manda sobre ese filtro inicial. No cambia permisos: la API decide qué
conversaciones lee cada persona.

## Webhook

El webhook de Kapso apunta a la API: `https://<api>/webhooks/kapso` (ver
[deployment.md](deployment.md)). Para una prueba local con autorización de exposición temporal, el
overlay `docker-compose.kapso-local.yml` publica sólo ese path a través de
`integrations/kapso/nginx.conf` (gateway → `recepcion-api`):

1. `docker compose -f docker-compose.yml -f docker-compose.hospital.yml -f docker-compose.kapso-local.yml up -d kapso-gateway kapso-tunnel`.
   Omitir el overlay Hospital si ese entorno no está conectado.
2. Leer la URL de `docker compose -f docker-compose.yml -f docker-compose.kapso-local.yml logs kapso-tunnel`
   y configurar `https://<túnel>.trycloudflare.com/webhooks/kapso` en Kapso. Sólo ese path acepta
   POST; los demás devuelven 404.
3. Enviar un WhatsApp desde el teléfono de prueba y comprobarlo en `/?view=inbox`.
4. Al terminar, desactivar el webhook temporal en Kapso y detener `kapso-tunnel` y `kapso-gateway`.

`scripts/connect-kapso-local.py` es una herramienta local de aprovisionamiento para operadores:
enlaza el número y su customer de Kapso al tenant, guarda secretos con permisos 600 en `.env` de la
API y mantiene desactivados los envíos. Nunca versionar `.env` ni `secrets/`.

## Conectar números sin IDs ni claves

Abrir `/whatsapp` desde **Agregar mi número** en la bandeja o en Configuración.
Sólo administradores de negocio/plataforma pueden iniciar y verificar conexiones.
El backend crea o recupera un customer Kapso exclusivo del tenant y genera un
setup link en español para números existentes, con Coexistence o dedicado y
facturación Meta `customer_managed`. No compra ni aprovisiona otro número.
El usuario completa personalmente los pasos de Meta en la página segura de Kapso.

Al volver, Recepción consulta los números CONNECTED del customer autenticado,
comprueba también su propiedad en cada resultado y los agrega de forma idempotente.
No confía en IDs ni estados incluidos en el redirect. Crea el webhook firmado de
cada número para `KAPSO_WEBHOOK_URL` usando el secreto del servidor; si falla,
muestra una advertencia y permite reintentar con **Verificar conexión**.
Los nuevos canales no activan el agente automático. La bandeja los
filtra por número (`channelId`).

`FRONTEND_URL` debe contener la URL HTTPS pública de Recepción para retornar
automáticamente desde Kapso a `/whatsapp`. En localhost el enlace no configura
redirect y el usuario vuelve a la pestaña; se consulta al recuperar el foco y cada
15 segundos durante hasta 20 comprobaciones. También se puede verificar a mano.
No se expone una ruta pública nueva en el túnel: se reutiliza `/webhooks/kapso`.
La sincronización necesita que la aplicación esté abierta o que el administrador
pulse Verificar conexión; no requiere un webhook de ciclo de vida del proyecto.

## Equipo y responsables

- **Bandeja de entrada** (`/?view=inbox`) es el espacio de gestión: responsable visible,
  transferencia, filtros por persona, estado, número y seguimiento del cliente.
- **Equipo** (`/?view=team`) muestra abiertas, pendientes y pospuestas por persona,
  incluyendo conversaciones de todos los números del hospital. Una persona puede tener
  varias conversaciones; cada conversación tiene un responsable principal.
- Los administradores pueden seleccionar hasta 100 conversaciones de una página
  para reasignarlas juntas. El servidor valida pertenencia, acceso y revisiones; si alguna
  cambió, no aplica ninguna asignación de ese lote. Cada transferencia crea actividad y auditoría.
- Un recepcionista puede tomar conversaciones sin responsable y transferir las suyas a
  compañeros activos; no puede tomar las de otra persona por asignación, macro o envío CRM.
- Sólo administradores desactivan/restauran acceso a Recepción. Deben reasignar primero el
  trabajo activo. Esto no modifica ni desactiva la cuenta compartida del Hospital.
- Las personas aparecen al iniciar sesión con la identidad de su hospital. La creación de
  cuentas y los cambios de rol siguen en el proveedor de identidad del Hospital: su contrato
  `/v1/clinic-users` todavía responde 501 y no se simula una invitación enviada.
- **WhatsApp · números** (`/whatsapp`) administra conexiones. **Consultar historial** abre la
  bandeja filtrada a ese número (`/?view=inbox&channelId=…`), en cualquier estado.
