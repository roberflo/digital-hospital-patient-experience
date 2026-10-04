# Conectar Recepción al Hospital de Easypanel

La misma instalación Hospital puede servir a varios hospitales. Recepción determina cuál corresponde por el `tenant_id` firmado del usuario; no se selecciona escribiendo un UUID ni se copian pacientes entre hospitales.

## 1. Preparar el despliegue una vez

Esto lo hace el operador una sola vez por instalación, no por hospital.

En el servicio de infraestructura de **Hospital**, definir y volver a ejecutar su job `keycloak-config`:

```text
KC_RECEPCION_WEB_CLIENT_SECRET=<secreto A>
KC_RECEPCION_WEB_REDIRECT_URIS=https://recepcion.tudominio.com/api/auth/callback/keycloak
KC_RECEPCION_WEB_WEB_ORIGINS=https://recepcion.tudominio.com
KC_RECEPCION_SERVICE_CLIENT_SECRET=<secreto B>
```

El job crea el cliente `recepcion-web` y, por cada hospital que tenga usuarios en el realm, su cuenta `recepcion-service-<UUID>` con exactamente los roles **Recepción** y `reception-agent`. Nadie copia UUIDs: Keycloak ya sabe qué hospitales existen. Un hospital nuevo recibe su cuenta en el siguiente despliegue de Hospital.

En la **API de Recepción** (con volumen persistente `/keys`, `ASPNETCORE_ENVIRONMENT=Production`):

```text
Auth__Authority=https://identidad.tudominio.com/realms/hospital
Auth__Audience=hospital-api
HOSPITAL_API_URL=http://proyecto_hospital-api:8080
HOSPITAL_SERVICE_CLIENT_SECRET=<secreto B>
HOSPITAL_ALLOWED_API_ORIGINS=http://proyecto_hospital-api:8080
HOSPITAL_PUBLIC_URL=https://hospital.tudominio.com
```

En la **web de Recepción**:

```text
KEYCLOAK_ISSUER=https://identidad.tudominio.com/realms/hospital
KEYCLOAK_CLIENT_ID=recepcion-web
KEYCLOAK_CLIENT_SECRET=<secreto A>
NEXTAUTH_URL=https://recepcion.tudominio.com
API_URL=http://servicio-api-recepcion:8080
AUTH_SECRET=<secreto propio de Recepción>
```

El issuer debe ser el mismo en ambos servicios. `KEYCLOAK_INTERNAL_ISSUER` es opcional para el backchannel interno. No hay variables por hospital, salvo la entrega automática de recetas, que se autoriza hospital por hospital ([hospital-integration.md](hospital-integration.md)).

## 2. Conectar un hospital

El Administrador del hospital entra a Recepción con su cuenta de Hospital. Eso es todo: su espacio se crea en ese momento, el hospital y los permisos salen de su identidad, y la conexión con la agenda y los pacientes queda activa sin escribir direcciones, identificadores ni secretos. Para que sólo el operador pueda abrir espacios, `HOSPITAL_SELF_ONBOARDING=false` y `BOOTSTRAP_TENANT_ID` / `BOOTSTRAP_TENANT_NAME`.

El negocio sólo sigue estos pasos:

1. Pulsar **Continuar con mi cuenta del hospital**. Se reutiliza la sesión abierta en Hospital; cuando no existe, se pide el acceso habitual.
2. Abrir **Mi hospital**. La identidad autenticada determina el hospital y los permisos; la disponibilidad se comprueba automáticamente consultando la agenda.
3. Pulsar **Abrir agenda**, **Vincular pacientes** o **Trabajar con tu equipo**.

No se piden URLs, identificadores ni secretos al negocio. Si el operador todavía no preparó el acceso para ese hospital, se muestra una explicación y se conserva la identidad correcta. Si Hospital no responde, **Volver a intentar** repite la comprobación sin cambiar la conexión. **Usar otra cuenta del hospital** solicita un nuevo inicio de sesión explícitamente.

Este acceso no otorga permisos clínicos al servicio ni activa agentes, recordatorios o envíos. La entrega automática de recetas usa el puente acotado y los permisos separados descritos en [hospital-integration.md](hospital-integration.md). Los doctores consultan documentos con su propia identidad del Hospital.

Google y WhatsApp requieren su propia autorización externa. WhatsApp ofrece el alta guiada de números. En Google se elige el calendario por nombre entre aquellos con permiso de escritura; no se copia su identificador. La selección se valida de nuevo en el servidor para el hospital de la sesión. Una autorización antigua de Google puede requerir reconexión para permitir listar calendarios. Referencia: [CalendarList.list](https://developers.google.com/workspace/calendar/api/v3/reference/calendarList/list).

## 3. Usar pacientes y equipo

- Los compañeros entran con su cuenta existente de Hospital y aparecen en **Equipo** automáticamente; nombres y roles se sincronizan al usar Recepción.
- **Contactos → Vincular paciente** permite buscar por nombre, DUI o número de expediente, elegir un resultado y confirmar. La coincidencia de nombre nunca vincula automáticamente.
- Al confirmar se verifica el teléfono completo y la pertenencia al hospital. No se crea un expediente clínico ficticio. Un teléfono distinto debe corregirse en Hospital.
- El vínculo registra al contacto como cliente y habilita sus citas. El expediente y las recetas siguen sujetos a permisos clínicos y asignación de conversación.

## Validación antes de abrir al equipo

Comprobar login real del administrador y un empleado; comprobación automática de **Mi hospital**; vínculo con paciente sintético; creación, consulta, reprogramación y cancelación de una cita verificando también Hospital. Probar que otra cuenta de hospital no pueda acceder al contacto ni a su paciente. La prueba de desarrollo está en `tests/hospital_connection_live.cjs`; utiliza cuentas sintéticas locales, no credenciales de producción.

No se ha realizado un despliegue remoto en Easypanel como parte de esta implementación: hacen falta los dominios y acceso al despliegue real. La conexión local se verifica por separado con Hospital C.

## Evidencia local de conexión

2026-10-03: acceso SSO del Administrador C existente, guardado de conexión comprobada desde la interfaz, búsqueda del paciente sintético del Hospital y rechazo de dirección no permitida sin reemplazar la configuración válida. Pruebas de cifrado y aislamiento de configuración, alta de tenant sólo por administrador del issuer esperado; suite Recepción144/144. Navegador72/72 incluyendo escritorio y móvil. Agenda CRUD real verificado con la conexión guardada.

2026-10-03, simplificación de acceso: Mi hospital comprueba automáticamente la agenda y reutiliza la sesión de Hospital, conservando nombre, hospital y permisos. Retirados los formularios de secretos e identificadores. Google ofrece calendarios por nombre, con validación de escritura y aislamiento por hospital; no se ha autorizado una cuenta real de Google durante estas pruebas. Verificados 145 tests backend, 106 comprobaciones API, 76 pruebas navegador escritorio/móvil y 12 tests servidor. Acceso SSO real y búsqueda del paciente sintético C comprobados por separado.
