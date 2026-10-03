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
cada conversación solicitada pertenece a ese número. Actualmente esta vista usa
un número configurado por despliegue; la bandeja CRM conserva sus múltiples canales.

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
