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

- `ASPNETCORE_ENVIRONMENT=Production`, `SEND_ENABLED=false` inicialmente.
- `ConnectionStrings__Database=Host=<servicio-db>;Database=recepcion;Username=recepcion;Password=<secreto>`.
- `KEY_DIRECTORY=/keys`, `PHONE_HASH_KEY`, `Auth__Authority`, `Auth__Audience=hospital-api`.
- `BOOTSTRAP_TENANT_ID`: UUID real de Hospital; `BOOTSTRAP_TENANT_NAME`: nombre del negocio inicial.
- `KAPSO_API_KEY`, `KAPSO_WEBHOOK_SECRET`, opcional `KAPSO_PHONE_NUMBER_ID` para asociar el primer número al tenant bootstrap.
- `NVIDIA_API_KEY`, `AI_BASE_URL=https://integrate.api.nvidia.com/v1`, `AI_MODEL=nvidia/nemotron-3-super-120b-a12b`.
- `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `GOOGLE_REDIRECT_URI=https://<api>/oauth/google/callback`, `FRONTEND_URL=https://<web>`.
- `HOSPITAL_API_URL`, `HOSPITAL_SERVICE_CLIENT_SECRET`, `HOSPITAL_ALLOWED_API_ORIGINS` y `HOSPITAL_PUBLIC_URL`, una vez por instalación ([hospital-easypanel.md](hospital-easypanel.md)). No hay configuración por hospital.

Web recibe únicamente `AUTH_SECRET`, `NEXTAUTH_SECRET` (mismo valor), `NEXTAUTH_URL=https://<web>`, `API_URL=http://<servicio-api>:8080`, `KEYCLOAK_ISSUER`, `KEYCLOAK_CLIENT_ID`, `KEYCLOAK_CLIENT_SECRET`. No necesita claves Kapso/NIM/Google ni acceso a PostgreSQL.

Las migraciones EF están versionadas en `backend/Migrations`. Tras respaldar, iniciar una sola réplica con `INITIALIZE_DATABASE=true` para aplicarlas; después poner `false`. Antes de retroceder una versión, comprobar compatibilidad del esquema; no ejecutar `migrations remove` ni borrar volúmenes en producción.

## Keycloak y hospitales

El realm `hospital` de Hospital es el único proveedor de identidad, en todos los entornos: Recepción no emite tokens, no guarda contraseñas y no tiene usuarios propios. Sus clientes no se crean a mano; los declara y verifica en cada arranque el job `keycloak-config` de Hospital (`infra/keycloak/configure-realms.sh` §9) cuando su `.env` define:

```text
KC_RECEPCION_WEB_CLIENT_SECRET=<secreto>                 # cliente recepcion-web
KC_RECEPCION_WEB_REDIRECT_URIS=https://<web>/api/auth/callback/keycloak
KC_RECEPCION_WEB_WEB_ORIGINS=https://<web>
KC_RECEPCION_SERVICE_CLIENT_SECRET=<secreto>             # clientes recepcion-service-<tenant>
KC_RECEPCION_SERVICE_TENANTS=<uuid> <uuid>               # un hospital por UUID
```

`recepcion-web` es confidencial, authorization code + PKCE, sin acceso directo por contraseña ni cuenta de servicio, y sólo puede afirmar los siete roles de Hospital más `platform-owner`. El access token incluye audiencia `hospital-api`, `sub`, `realm_access.roles` y, salvo para el dueño de plataforma, `tenant_id`.

| Rol en Keycloak (realm `hospital`) | En Recepción | Puede |
|---|---|---|
| `Administrador` | Administrador | Todo lo operativo: equipo, canales, agente, recordatorios, auditoría, cola, respuestas guardadas, macros, reasignación masiva. Sin acceso clínico. |
| `Recepción`, `Admisión` | Recepcionista | Bandeja, contactos, agenda, seguimiento comercial. Transfiere sólo sus conversaciones. |
| `Médicos`, `Odontólogos`, `Nutricionistas` | Doctor | Sus conversaciones asignadas y, con su propia identidad, el expediente del paciente vinculado. |
| `Enfermería` | — | Sin acceso a Recepción. |
| `reception-agent` | — | Capacidad de la cuenta de servicio, nunca de una persona. |
| `platform-owner` (sin `tenant_id`) | Dueño de plataforma | Sólo configurar, en nombre de la recepción que elige: conexión con Hospital y WhatsApp (generar el enlace, sincronizar, pausar un número y habilitarlo si el agente de esa recepción está apagado). Sin bandeja, contactos, pacientes, rutas clínicas, ajustes ni activar el agente. No es miembro de ningún hospital. |

No existen otros roles ni equivalencias: un token sin uno de los seis que dan acceso, y que no sea de plataforma, recibe 403. Los usuarios se materializan al iniciar sesión; un `sub` ya vinculado a un hospital no puede cambiar a otro con un token diferente. Los roles se administran en Keycloak; Recepción sólo permite desactivar el acceso de un miembro.

El espacio de cada hospital lo abre su Administrador al entrar por primera vez (`HOSPITAL_SELF_ONBOARDING=true`) o el operador con `BOOTSTRAP_TENANT_ID` / `BOOTSTRAP_TENANT_NAME`. Cada usuario de hospital pertenece a una sola empresa y no ve selector.

El único selector es el del dueño de plataforma ([platform-owner.md](platform-owner.md)): al entrar elige una recepción de `GET /api/platform/tenants` y la web muestra «Actuando en nombre de ‹recepción›» con «Cambiar». La elección se guarda en la cookie `recepcion.acting` (`__Secure-recepcion.acting` bajo https): HttpOnly, SameSite=Lax, doce horas, cifrada con `AUTH_SECRET` y ligada al `sub` de quien eligió; nunca en `localStorage` ni en la URL. Sólo el proxy del servidor web la convierte en la cabecera `X-Acting-Tenant`, tras validar el id contra la lista leída en servidor; una cabecera enviada por el navegador no se reenvía, y la API la rechaza en un token que no sea de plataforma. Como la cookie es de todo el navegador y no de una pestaña, cada petición de la vista dice qué recepción tiene pintada (`X-Acting-Expected`, que el proxy consume y no reenvía): si no es la sellada, el proxy responde 409 sin llamar a la API y la pestaña deja de operar («Cambió de recepción en otra pestaña»). El dueño no abre el espacio de un hospital: eso sigue siendo del Administrador o de `BOOTSTRAP_TENANT_ID`. No requiere variables nuevas en Recepción; el rol lo emite el job `keycloak-config` de Hospital (`KC_PLATFORM_OWNERS`). Cada acto en nombre queda en `Audit` con la recepción objetivo y el actor `platform:<sub>`. Si a la instalación le falta `KAPSO_API_KEY`, `KAPSO_WEBHOOK_URL` o `KAPSO_WEBHOOK_SECRET`, la vista lo nombra como tarea del operador y no ofrece conectar.

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

El proveedor está encapsulado tras la API de Chat Completions compatible con OpenAI. Para migrarlo, cambiar URL/modelo/credencial y ejecutar las pruebas de herramientas del nuevo proveedor; El agente de WhatsApp corre sobre Microsoft Agent Framework con el cliente compatible con OpenAI: NIM primero y, si `OPENAI_API_KEY` y `OPENAI_MODEL` están configuradas, OpenAI repite una llamada fallida. El modelo NIM se elige con `scripts/eval-agent.sh <modelo>`; el elegido es `nvidia/nemotron-3-super-120b-a12b` ([resultados](reception-agent.md)). El agente ya no envía `chat_template_kwargs`; el asistente interno sí.

El agente de WhatsApp envía al proveedor la guía de atención, hasta 24 mensajes del hilo y resultados de herramientas de ese paciente, incluidas instrucciones de recetas cuando se solicitan. La aplicación comprueba identidad y permisos antes de entregarlos. El asistente interno utiliza sólo contadores anónimos y la pregunta escrita por el usuario; el ID del contacto seleccionado no se incluye en el prompt. Ningún modelo puede cambiar tenant o URL de Hospital.

## Operación y respaldo

- Comprobar `/health/live`, `/health/ready` y el inicio de sesión después de cada despliegue.
- Revisar `/api/jobs` y `/api/audit` como Administrador; `failed` o `uncertain` requieren seguimiento humano. Una entrega incierta conserva el intento y no se reenvía automáticamente: consultar WhatsApp/agenda antes de repetir.
- Usar `scripts/backup.sh` con Compose o el equivalente en Easypanel. Guarda un dump de PostgreSQL y el keyring `/keys`. Mantener copias cifradas fuera del host y probar restauración en un entorno separado.
- Restauración: detener API, restaurar el dump con `pg_restore` en una base vacía, restaurar `/keys` con dueño UID 1654, conservar el mismo `PHONE_HASH_KEY`, y reiniciar la API de una versión compatible. Nunca restaurar sobre datos activos sin respaldo y ventana de mantenimiento.
- Perder `/keys` impide descifrar contactos/mensajes/notas/tokens. Cambiar `PHONE_HASH_KEY` exige recalcular los índices de teléfonos; no cambiarlo como si fuera una contraseña ordinaria.
- Logs operativos omiten cuerpos de mensajes y errores detallados de proveedores. Proteger también los volúmenes, copias de seguridad, acceso al host y retención de datos.

## Aceptación antes de pacientes reales

Completar login/renovación con Keycloak real, dos empresas reales aisladas, agenda y bridge clínico con stores reales, PDF de receta firmada, Google OAuth con una cuenta de prueba, coexistencia de un número elegible y prueba controlada de envío/recepción. Validar los horarios de médicos y el flujo operativo con personal del hospital. La agenda Hospital permite solapamientos de forma intencional: el CRM vuelve a consultar disponibilidad y deriva conflictos, pero no puede garantizar bloqueo atómico de slots que Hospital aún no ofrece.

## Conexión guiada al Hospital existente

Usar [hospital-easypanel.md](hospital-easypanel.md) para configurar el acceso compartido una sola vez y conectar cada hospital desde **Mi hospital**. El formulario valida credenciales y tenant antes de guardar la conexión cifrada; evita editar variables por UUID. El alta automática de espacios requiere `HOSPITAL_SELF_ONBOARDING=true` y un Administrador autenticado por Hospital.
