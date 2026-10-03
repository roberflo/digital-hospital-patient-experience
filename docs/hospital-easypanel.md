# Conectar Recepción al Hospital de Easypanel

La misma instalación Hospital puede servir a varios hospitales. Recepción determina cuál corresponde por el `tenant_id` firmado del usuario; no se selecciona escribiendo un UUID ni se copian pacientes entre hospitales.

## 1. Preparar el despliegue una vez

Crear los servicios web, API y PostgreSQL de [deployment.md](deployment.md), con volumen persistente `/keys` en API. Mantener `ALLOW_DEV_LOGIN=false` y `ASPNETCORE_ENVIRONMENT=Production`. No copiar la configuración local ni habilitar envíos durante el montaje.

En la API de Recepción configurar:

```text
Auth__Authority=https://identidad.tudominio.com/realms/hospital
Auth__Audience=hospital-api
HOSPITAL_ALLOWED_API_ORIGINS=https://api-hospital.tudominio.com
HOSPITAL_PUBLIC_URL=https://hospital.tudominio.com
HOSPITAL_SELF_ONBOARDING=true
```

`HOSPITAL_ALLOWED_API_ORIGINS` puede contener varias direcciones base separadas por comas. Una dirección interna de Easypanel como `http://proyecto_hospital-api:8080` también sirve si ambas APIs comparten la red. Copiar el nombre real del servicio; no usar los nombres Docker de este equipo local. Sólo esos destinos reciben el token de la cuenta de servicio. La URL pública de la interfaz Hospital debe usar HTTPS.

`HOSPITAL_SELF_ONBOARDING=true` permite únicamente a un Administrador autenticado por el Hospital inicializar su propio espacio en Recepción. Los demás empleados entran después. Si se prefiere el alta por operador, dejarlo en `false` y usar `BOOTSTRAP_TENANT_ID` / `BOOTSTRAP_TENANT_NAME`.

En la web de Recepción configurar el mismo proveedor:

```text
KEYCLOAK_ISSUER=https://identidad.tudominio.com/realms/hospital
KEYCLOAK_CLIENT_ID=recepcion-web
KEYCLOAK_CLIENT_SECRET=<secreto del cliente web>
NEXTAUTH_URL=https://recepcion.tudominio.com
API_URL=http://servicio-api-recepcion:8080
AUTH_SECRET=<secreto propio de Recepción>
```

En Keycloak crear el cliente confidencial `recepcion-web`, flujo Authorization Code + PKCE S256, callback exacto `https://recepcion.tudominio.com/api/auth/callback/keycloak`, origen web exacto y scopes `openid`, `profile`, `email`, `roles`, `tenant-context`. El token debe conservar `sub`, `tenant_id`, roles del Hospital y audiencia `hospital-api`. No crear otros usuarios ni contraseñas. El cliente web no necesita cuenta de servicio ni acceso administrativo a Keycloak.

El issuer público de ambos servicios debe coincidir. `KEYCLOAK_INTERNAL_ISSUER` es opcional para el backchannel interno; debe apuntar al mismo realm, nunca a un proveedor distinto. Reiniciar los servicios tras cambiar variables.

## 2. Conectar cada hospital desde Recepción

1. Entrar con **Continuar con mi cuenta del hospital**, usando una cuenta Administrador del Hospital.
2. Abrir **Mi hospital → Conectar o actualizar Hospital**.
3. Seleccionar la API permitida y escribir la dirección pública de Hospital.
4. Introducir el ID y secreto de la cuenta de servicio creada para Recepción en ese hospital.
5. Pulsar **Comprobar y conectar**. Se consulta la agenda real antes de guardar. Si falla el acceso o el `tenant_id` no corresponde a la sesión, se conserva la conexión anterior.

La cuenta de servicio es un cliente confidencial de Keycloak con service accounts, sin login interactivo, sin password grant, sin full-scope. Debe incluir audiencia `hospital-api`, claim `tenant_id` fijo del hospital y rol **Recepción**. El ID/secreto de esta cuenta son distintos al cliente web de login. El administrador de identidad prepara esta cuenta una vez; el usuario del negocio no necesita editar variables por UUID.

El secreto queda cifrado en PostgreSQL con Data Protection. Debe preservarse `/keys` al reiniciar o migrar: sin ese volumen no se pueden descifrar las conexiones. No se devuelve el secreto en las respuestas ni se guarda en almacenamiento del navegador. La configuración guardada prevalece sobre la configuración histórica por variables del mismo hospital.

Este formulario conecta agenda, pacientes y CRM comercial. No otorga permisos clínicos al servicio ni activa agentes, recordatorios o envíos. La entrega automática de recetas usa el puente acotado y los permisos separados descritos en [hospital-integration.md](hospital-integration.md). Los doctores consultan documentos con su propia identidad del Hospital.

## 3. Usar pacientes y equipo

- Los compañeros entran con su cuenta existente de Hospital y aparecen en **Equipo** automáticamente; nombres y roles se sincronizan al usar Recepción.
- **Contactos → Vincular paciente** permite buscar por nombre, DUI o número de expediente, elegir un resultado y confirmar. La coincidencia de nombre nunca vincula automáticamente.
- Al confirmar se verifica el teléfono completo y la pertenencia al hospital. No se crea un expediente clínico ficticio. Un teléfono distinto debe corregirse en Hospital.
- El vínculo registra al contacto como cliente y habilita sus citas. El expediente y las recetas siguen sujetos a permisos clínicos y asignación de conversación.

## Validación antes de abrir al equipo

Comprobar login real del administrador y un empleado; botón **Comprobar conexión**; vínculo con paciente sintético; creación, consulta, reprogramación y cancelación de una cita verificando también Hospital. Probar que otra cuenta de hospital no pueda acceder al contacto ni a su paciente. La prueba de desarrollo está en `tests/hospital_connection_live.cjs`; utiliza cuentas sintéticas locales, no credenciales de producción.

No se ha realizado un despliegue remoto en Easypanel como parte de esta implementación: hacen falta los dominios y acceso al despliegue real. La conexión local se verifica por separado con Hospital C.

## Evidencia local de conexión

2026-10-03: acceso SSO del Administrador C existente, guardado de conexión comprobada desde la interfaz, búsqueda del paciente sintético del Hospital y rechazo de dirección no permitida sin reemplazar la configuración válida. Pruebas de cifrado y aislamiento de configuración, alta de tenant sólo por administrador del issuer esperado; suite Recepción144/144. Navegador72/72 incluyendo escritorio y móvil. Agenda CRUD real verificado con la conexión guardada.
