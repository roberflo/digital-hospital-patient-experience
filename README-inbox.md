# Bandeja WhatsApp de Kapso

`/inbox` consulta conversaciones y mensajes directamente a Kapso. La bandeja CRM
`/?view=inbox` conserva asignación, notas, etiquetas y seguimiento del hospital.
El enlace “WhatsApp ↗” permite entrar al historial del proveedor. Esta nueva
bandeja no crea una base de datos de mensajes. Los contadores de no leídos son
locales a la sesión de la pestaña; no marcan mensajes como leídos en WhatsApp.

## Configuración

Para desarrollo con `npm run dev`, copiar `frontend/.env.example` a
`frontend/.env.local`. Para Docker, completar `.env` en la raíz; Compose inyecta
las variables exclusivamente al servidor:

- `KAPSO_API_KEY`: clave del proyecto (cabecera X-API-Key).
- `KAPSO_PHONE_NUMBER_ID`: ID del número conectado.
- `KAPSO_WEBHOOK_SECRET`: secreto del webhook; conservarlo al crearlo.
- `KAPSO_MANUAL_SEND_ENABLED=false`: activar explícitamente con `true` cuando se
  autorice enviar respuestas manuales. No activa el agente automático de .NET.
- `API_URL`, `AUTH_SECRET`, `NEXTAUTH_URL` y credenciales Keycloak: autenticación
  existente de Recepción. No usar variables `NEXT_PUBLIC_` para credenciales.

El número debe pertenecer al hospital del usuario en `/api/channels`. Se reutiliza
la validación de sesión, renovación de tokens y comprobación de membresía del BFF.
No basta con tener una sesión de cualquier hospital. La API verifica también que
cada conversación solicitada pertenece a ese número. La bandeja permite elegir entre los números del hospital; cada API y stream SSE
valida el número seleccionado contra los canales autorizados.

## Webhook

Crear en Kapso un webhook de tipo Kapso, payload **v2**, para el número configurado,
con URL `https://TU-DOMINIO/api/kapso/webhook`. Seleccionar:

- `whatsapp.message.received`, `whatsapp.message.sent`
- `whatsapp.message.delivered`, `whatsapp.message.read`, `whatsapp.message.failed`
- `whatsapp.conversation.created`, `whatsapp.conversation.ended`

Guardar el secreto en el entorno y recrear `web`. La firma HMAC-SHA256 debe llegar
como `X-Webhook-Signature` hexadecimal sobre los bytes originales. El receptor
rechaza firmas inválidas (401), versiones incompatibles (400) y cuerpos mayores
que 2 MiB (413). Procesa batches y deduplica `X-Idempotency-Key` durante una hora
(hasta 10.000 entradas). No registra cuerpos, claves ni mensajes en logs.

Para mantener además la gestión CRM existente, dirigir el webhook al gateway de
`integrations/kapso/nginx.conf`: `/webhooks/kapso` entrega al receptor .NET existente
y refleja los mismos bytes firmados al nuevo receptor Next.js. La réplica hacia
Next no bloquea el resultado del CRM; si se pierde un evento, la bandeja consulta
Kapso cada 15 segundos. No se crean webhooks duplicados para el mismo flujo.

## Prueba local

1. Iniciar la app y usar un usuario del hospital al que pertenece el número.
2. Para un Sandbox, seleccionar el número sandbox en Kapso, seguir las instrucciones
   de vinculación de su consola y configurar su ID y webhook. Para producción,
   utilizar únicamente un número de prueba autorizado por el operador.
3. Con autorización para exposición temporal, iniciar el overlay:
   `docker compose -f docker-compose.yml -f docker-compose.hospital.yml -f docker-compose.kapso-local.yml up -d kapso-gateway kapso-tunnel`.
   Omitir el overlay Hospital si ese entorno no está conectado.
4. Leer la URL de `docker compose -f docker-compose.yml -f docker-compose.kapso-local.yml logs kapso-tunnel`.
   Configurar `https://<túnel>.trycloudflare.com/webhooks/kapso` en Kapso. Sólo ese
   path acepta POST; otros paths públicos devuelven 404. No publicar todo el CRM.
   Un ngrok HTTPS puede apuntar al mismo gateway con el puerto ligado a localhost.
5. Abrir `/inbox` y enviar un WhatsApp desde el teléfono de prueba al número
   conectado. Confirmar el contacto, mensaje, ventana de 24h y actualización SSE.
6. Tras habilitar envío manual expresamente, responder desde el chat y comprobar
   entrega/lectura en ambos extremos. Fuera de 24h se bloquea texto libre en UI
   **y servidor**; envío de plantillas no forma parte de esta pantalla.
7. Al terminar la prueba, desactivar el webhook temporal en Kapso y detener
   `kapso-tunnel` y `kapso-gateway`. La URL temporal cambia si se recrea el túnel.

`connect-kapso-local.py` es una herramienta local de aprovisionamiento para
operadores: enlaza el número y su customer de Kapso al tenant, guarda secretos
con permisos 600 y mantiene desactivados los envíos. Nunca versionar `.env` ni
`secrets/`. La autorización del envío manual es independiente de `SEND_ENABLED`.

## Tiempo real y límites operativos

`lib/events.ts` aísla el bus en `globalThis`. Requiere **un único proceso Node
persistente** (`next start`, contenedor Docker o VPS). La deduplicación en memoria
se pierde al reiniciar; el historial sigue en Kapso. Antes de escalar a varias
réplicas o desplegar en Vercel/serverless, sustituir ese módulo por Pusher, Ably,
Supabase Realtime u otro broker compartido, manteniendo autenticación y filtro por
hospital. El SSE envía un ping cada 25s, limpia listeners al desconectar y renueva
la conexión cada 55s para volver a validar la membresía. La UI consulta la lista
y el chat cada 15s como respaldo.

Se muestran placeholders para archivos/audio/otros medios; descargar o reproducir
medios y enviar plantillas queda en los flujos existentes o una ampliación posterior.
Los contactos sin teléfono se identifican por UUID y se pueden leer, pero no
reciben texto mediante este formulario. Un envío incierto no se reintenta
automáticamente: comprobar el historial antes de repetirlo.

## Verificación

Desde `frontend`: `npm run typecheck`, `npm run lint`, `npm run test:server`,
`npm run build`, y con la app local activa `npx playwright test kapso-inbox.spec.ts`.
Los tests del proveedor usan mocks y nunca envían WhatsApps reales.

La prueba opt-in `python3 tests/kapso_live.py` comprueba el aislamiento entre
hospitales y el recorrido público firmado hasta SSE; requiere la clínica sintética
local y el túnel autorizado. No envía WhatsApps ni crea mensajes de pacientes.

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
Los nuevos canales no activan el agente automático. La bandeja manual permite
seleccionarlos respetando permisos y ventana de 24h.

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
- **WhatsApp · números** (`/whatsapp`) administra conexiones. El acceso secundario
  **Consultar historial** (`/inbox`) conserva la consulta y el envío manual autorizado de Kapso;
  su enlace **Asignar y gestionar en bandeja** filtra por teléfono y número del mismo hospital.
  El historial anterior a la recepción de webhooks puede no existir todavía en el CRM.
  El envío directo de Kapso no aplica asignaciones del CRM; para coordinar el equipo se usa la
  bandeja CRM. Las compuertas existentes de envío y agente automático no cambian.
