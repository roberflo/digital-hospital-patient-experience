# Despliegue y operación

## Servicios en Easypanel

Conectar el repositorio `roberflo/digital-hospital-patient-experience`, rama `main`. Crear los tres servicios siguientes en el mismo proyecto/red privada. No se ha accedido ni desplegado al Easypanel real desde esta tarea.

| Servicio | Construcción / imagen | Puerto interno | Persistencia |
|---|---|---|---|
| db | `postgres:17-alpine` | 5432, privado | `/var/lib/postgresql/data` |
| api | contexto `backend`, Dockerfile `backend/Dockerfile` | 8080 | volumen permanente `/keys`, escribible por UID 1654 |
| web | contexto `frontend`, Dockerfile `frontend/Dockerfile` | 3000 | ninguna |

La ruta de Dockerfile se interpreta respecto al contexto elegido en Easypanel: si el contexto ya es `backend` o `frontend`, usar `Dockerfile`. PostgreSQL y la API no necesitan exponerse directamente al navegador: éste usa el BFF de Next.js. La API sí necesita un dominio HTTPS para el webhook Kapso y callback Google. Restringir el ingreso público de la API a `/webhooks/kapso`, `/oauth/google/callback` y, si se requiere, health; el tráfico `/api/*` va por red privada desde web.

Usar una réplica inicial de API. El worker tiene reclamación atómica de jobs y bloqueos PostgreSQL, pero no se ha hecho prueba de carga distribuida. No compartir la base CRM con las tablas de Hospital.

## Variables de producción

Copiar la estructura de `.env.example` al almacén de secretos del despliegue. Generar valores independientes de al menos 32 bytes para `AUTH_SECRET`, `PHONE_HASH_KEY` y contraseña PostgreSQL. No copiar el `.env` de demostración ni publicar las claves en Git.

API:

- `ASPNETCORE_ENVIRONMENT=Production`, `ALLOW_DEV_LOGIN=false`, `SEND_ENABLED=false` inicialmente.
- `ConnectionStrings__Database=Host=<servicio-db>;Database=recepcion;Username=recepcion;Password=<secreto>`.
- `KEY_DIRECTORY=/keys`, `PHONE_HASH_KEY`, `Auth__Authority`, `Auth__Audience=hospital-api`.
- `BOOTSTRAP_TENANT_ID`: UUID real de Hospital; `BOOTSTRAP_TENANT_NAME`: nombre del negocio inicial.
- `KAPSO_API_KEY`, `KAPSO_WEBHOOK_SECRET`, opcional `KAPSO_PHONE_NUMBER_ID` para asociar el primer número al tenant bootstrap.
- `NVIDIA_API_KEY`, `AI_BASE_URL=https://integrate.api.nvidia.com/v1`, `AI_MODEL=nvidia/nemotron-3-super-120b-a12b`.
- `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `GOOGLE_REDIRECT_URI=https://<api>/oauth/google/callback`, `FRONTEND_URL=https://<web>`.
- Configuración `Hospital__Tenants__<UUID>__...` de [hospital-integration.md](hospital-integration.md).

Web recibe únicamente `AUTH_SECRET`, `NEXTAUTH_SECRET` (mismo valor), `NEXTAUTH_URL=https://<web>`, `API_URL=http://<servicio-api>:8080`, `KEYCLOAK_ISSUER`, `KEYCLOAK_CLIENT_ID`, `KEYCLOAK_CLIENT_SECRET`, `ASPNETCORE_ENVIRONMENT=Production`, `ALLOW_DEV_LOGIN=false`. No necesita claves Kapso/NIM/Google ni acceso a PostgreSQL. El login se resuelve en runtime, no queda compilado en modo demo.

Las migraciones EF están versionadas en `backend/Migrations`. Tras respaldar, iniciar una sola réplica con `INITIALIZE_DATABASE=true` para aplicarlas; después poner `false`. Antes de retroceder una versión, comprobar compatibilidad del esquema; no ejecutar `migrations remove` ni borrar volúmenes en producción.

## Keycloak y hospitales

Usar el realm actual de Hospital. Crear un cliente confidential para web, flujo authorization code y redirect URI exacto `https://<web>/api/auth/callback/keycloak`; web origins exactos y acceso directo por contraseña desactivado. El access token debe incluir audiencia de la API, `sub`, `tenant_id` UUID y `realm_access.roles`.

Roles CRM: `platform_admin`, `admin`, `supervisor`, `agent`, `doctor`. Se reconocen también `Administrador`, `Recepción`, `Admisión`, `Médicos`, `Odontólogos` y `Nutricionistas` para compartir el directorio existente. Los usuarios se materializan en CRM al iniciar sesión; un `sub` ya vinculado a una empresa no puede cambiar a otra con un token diferente. Los roles se administran en Keycloak; CRM permite desactivar el acceso de miembros.

Un `platform_admin` autenticado puede crear el registro de otra empresa mediante `POST /api/platform/tenants` con `{id,name}`. El UUID debe coincidir con Hospital y con el mapper de sus usuarios. Después configurar su cliente de servicio Hospital independiente. No hay selector de empresas para usuarios; cada usuario pertenece a una sola.

Desplegar el [puente de recetas](../integrations/hospital/README.md) y configurar el `azp` y `sub` reales antes de habilitar entrega clínica. El bot usa `reception-agent` y, para agenda, `Recepción`; nunca necesita un rol médico. La revisión y smoke del bridge con stores reales siguen siendo requisito previo a su habilitación.

## WhatsApp y coexistencia

1. En Configuración → Conectar con Kapso, el administrador genera un cliente Kapso para su empresa o recupera el existente mediante `external_customer_id` igual al UUID del tenant. Una asociación manual previa puede configurarse con `Kapso__Tenants__<UUID>__CustomerId`.
2. Completar Embedded Signup con el negocio; configurar coexistencia para los números elegibles de los doctores. Registrar el identificador verificado, nombre del canal y doctor. El doctor debe haber iniciado sesión y tener rol `doctor`; su identificador es el `sub` compartido con Hospital.
3. Registrar un webhook Kapso dirigido a `https://<api>/webhooks/kapso`. Guardar el secreto de firma correspondiente en `KAPSO_WEBHOOK_SECRET`. Suscribir `whatsapp.message.received`, `whatsapp.message.sent`, `whatsapp.message.delivered`, `whatsapp.message.read`, `whatsapp.message.failed` y `whatsapp.thread.standby`. El receptor usa HMAC SHA256 del body sin modificar, `X-Webhook-Signature`, `X-Webhook-Event` y `X-Idempotency-Key`; acepta entregas individuales o batch.
4. Probar con un teléfono controlado: entrante, respuesta, duplicado de webhook, takeover humano y eco desde Business App. Activar el canal, la guía y el agente; poner `SEND_ENABLED=true` únicamente en ese entorno configurado.

El número suministrado durante el desarrollo se verificó como **sandbox**, `is_coexistence=false`; no representa un número de doctor conectado en coexistencia. No se enviaron mensajes a pacientes durante el desarrollo ni se alteraron webhooks externos existentes. Los mensajes fuera de la ventana de 24 horas deben iniciarse con plantilla aprobada desde Kapso; la UI actual no administra campañas ni plantillas.

## Google Calendar

En Google Cloud, habilitar Calendar API, crear un OAuth client web y registrar el redirect exacto indicado arriba. En Configuración, escribir el ID del calendario compartido del hospital y conectar una cuenta con permisos de escritura. Cada doctor se identifica en el título del evento. Se usa el scope `calendar.events`, refresh token cifrado y estado de un solo uso que expira en diez minutos.

Hospital es la autoridad. El worker sincroniza los próximos 31 días cada 15 minutos; también se puede iniciar la sincronización manual de siete días. Alta y cambios usan una clave estable por tenant/cita; cancelaciones de paciente/hospital y registros por error eliminan el evento. No se envían nombres, teléfonos ni motivos de consulta de pacientes a Google. Editar en Google no cambia Hospital. Si se mueve una cita fuera del horizonte de 31 días, su espejo se actualizará al consultar ese rango; para una conciliación global haría falta ampliar el contrato de cambios de Hospital.

## NIM y alcance del agente

El proveedor está encapsulado tras la API de Chat Completions compatible con OpenAI. Para migrarlo, cambiar URL/modelo/credencial y ejecutar las pruebas de herramientas del nuevo proveedor; `chat_template_kwargs` corresponde a NIM y puede requerir ajuste. El modelo probado es `nvidia/nemotron-3-super-120b-a12b`, con tools y pensamiento deshabilitado para las respuestas breves.

El agente de WhatsApp envía al proveedor la guía de atención, hasta 24 mensajes del hilo y resultados de herramientas de ese paciente, incluidas instrucciones de recetas cuando se solicitan. La aplicación comprueba identidad y permisos antes de entregarlos. El asistente interno utiliza sólo contadores anónimos y la pregunta escrita por el usuario; el ID del contacto seleccionado no se incluye en el prompt. Ningún modelo puede cambiar tenant o URL de Hospital.

## Operación y respaldo

- Comprobar `/health/live`, `/health/ready` y el inicio de sesión después de cada despliegue.
- Revisar `/api/jobs` y `/api/audit` como supervisor/admin; `failed` o `uncertain` requieren seguimiento humano. Una entrega incierta conserva el intento y no se reenvía automáticamente: consultar WhatsApp/agenda antes de repetir.
- Usar `scripts/backup.sh` con Compose o el equivalente en Easypanel. Guarda un dump de PostgreSQL y el keyring `/keys`. Mantener copias cifradas fuera del host y probar restauración en un entorno separado.
- Restauración: detener API, restaurar el dump con `pg_restore` en una base vacía, restaurar `/keys` con dueño UID 1654, conservar el mismo `PHONE_HASH_KEY`, y reiniciar la API de una versión compatible. Nunca restaurar sobre datos activos sin respaldo y ventana de mantenimiento.
- Perder `/keys` impide descifrar contactos/mensajes/notas/tokens. Cambiar `PHONE_HASH_KEY` exige recalcular los índices de teléfonos; no cambiarlo como si fuera una contraseña ordinaria.
- Logs operativos omiten cuerpos de mensajes y errores detallados de proveedores. Proteger también los volúmenes, copias de seguridad, acceso al host y retención de datos.

## Aceptación antes de pacientes reales

Completar login/renovación con Keycloak real, dos empresas reales aisladas, agenda y bridge clínico con stores reales, PDF de receta firmada, Google OAuth con una cuenta de prueba, coexistencia de un número elegible y prueba controlada de envío/recepción. Validar los horarios de médicos y el flujo operativo con personal del hospital. La agenda Hospital permite solapamientos de forma intencional: el CRM vuelve a consultar disponibilidad y deriva conflictos, pero no puede garantizar bloqueo atómico de slots que Hospital aún no ofrece.

## Conexión guiada al Hospital existente

Usar [hospital-easypanel.md](hospital-easypanel.md) para configurar el acceso compartido una sola vez y conectar cada hospital desde **Mi hospital**. El formulario valida credenciales y tenant antes de guardar la conexión cifrada; evita editar variables por UUID. El alta automática de espacios requiere `HOSPITAL_SELF_ONBOARDING=true` y un Administrador autenticado por Hospital.
