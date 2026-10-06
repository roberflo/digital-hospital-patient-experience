# Dueño de plataforma en Recepción

Petición del propietario el 2026-10-05: «en hospital administras como plataforma a los hospitales y en recepcion administras las recepciones para apoyar con configurar el telefono por ejemplo». Estado: **aceptada**. La identidad de plataforma (rol de realm `platform-owner`, sin `tenant_id`, emitido por el job `keycloak-config`) vive en Hospital: `docs/features/14-administracion-de-usuarios/plataforma-actuar-en-nombre.spec.md` (PLT-1).

Dos entregas: **REC-1** (entrar y elegir una recepción) y **REC-2** (conexión con Hospital y WhatsApp en nombre).

## Decisiones

1. **La recepción elegida viaja en la cabecera `X-Acting-Tenant`, solo para el rol de plataforma**, igual que en Hospital. Enmienda «el tenant solo sale del JWT» de `hospital-integration.md` con esa única excepción.
2. **El dueño de plataforma solo configura**: conexión con Hospital y WhatsApp. No abre bandeja, conversaciones, contactos, pacientes ni rutas clínicas, y no activa el agente.
3. **El dueño puede generar la conexión de WhatsApp (cliente Kapso) de una recepción en su nombre**; es lo que pide «configurar el teléfono».

## Invariantes

- **INV-R1** El actor de plataforma nunca es un `Member` ni abre el espacio de un hospital.
- **INV-R2** Negación por defecto: un actor de plataforma solo alcanza las rutas de la lista de esta spec; el resto → 403.
- **INV-R3** `MapRole` nunca devuelve `admin`, `doctor` ni `agent` para `platform-owner`.
- **INV-R4** La recepción elegida debe existir en `Tenants`; si no → 404.
- **INV-R5** Un token con `platform-owner` y `tenant_id` se rechaza; la cabecera en un token que no es de plataforma se rechaza, no se ignora.

## REC-1 — entrar y elegir una recepción

| AC | Criterio | Cómo se prueba |
|---|---|---|
| 1 | (Hospital) `recepcion-web` puede afirmar los siete roles **más** `platform-owner`, y el job sigue verificando «exactamente» esa lista | Editar a propósito la prueba del tope de roles en `RecepcionRealmScriptTests` (es la política que cambia); la que prohíbe roles clínicos y `Administrador` en las cuentas de servicio sigue verde |
| 2 | Token de plataforma sin recepción elegida: se admite solo para `GET /api/platform/tenants`; no crea `Tenant` ni `Member` | Prueba nueva en `backend.Tests/SecurityTests.cs`; afirma que `Members` y `Tenants` no cambian |
| 3 | INV-R3 | Caso nuevo junto a las pruebas existentes de `MapRole` |
| 4 | `GET /api/platform/tenants` → por recepción: id, nombre, hospital conectado, WhatsApp conectado, sin secretos. Otro rol → 403 | Prueba nueva de endpoint |
| 5 | Con recepción elegida, el inquilino del acto es esa; elegida inexistente → 404; token de hospital con la cabecera → rechazado; INV-R5 | `SecurityTests`, dos inquilinos |
| 6 | INV-R2: por cada ruta `/api/**` mapeada, el actor de plataforma recibe 403 salvo la lista (REC-1: AC 4 y la lectura del estado de conexión con Hospital) | Prueba nueva que enumera los endpoints del host; si el arnés no los expone, se recorre una lista escrita y se dice |
| 7 | Bandeja, conversaciones, mensajes, contactos, pacientes y rutas clínicas → 403 | Casos nominales dentro de AC 6 |
| 8 | Frontend: selector de recepciones al entrar; elección en cookie HttpOnly del servidor, nunca en `localStorage` ni URL; la cabecera la pone solo el proxy del servidor; aviso «Actuando en nombre de ‹recepción›» con «Cambiar»; sin bandeja en la navegación | Pruebas del proxy y de la vista |
| 9 | `hospital-integration.md` y `deployment.md` («No hay selector de empresas») se corrigen | Revisión del diff |

## REC-2 — conexión con Hospital y WhatsApp en nombre

| AC | Criterio | Cómo se prueba |
|---|---|---|
| 10 | Con recepción elegida el dueño puede: leer el estado de la instalación, listar los canales, iniciar la conexión de WhatsApp, sincronizar, pausar un número, habilitarlo **solo si el agente de esa recepción está apagado** (si está activo → 409: habilitar el número haría contestar al agente) y leer el diagnóstico del canal | Ampliar `WhatsAppOnboardingTests` con casos de plataforma |
| 11 | Las protecciones existentes (no confiar en el filtro del proveedor para un cliente ajeno; un número no se reasigna desde otro hospital) siguen verdes con el actor de plataforma | Las mismas pruebas, con el actor de plataforma |
| 12 | Leer, guardar y comprobar la conexión con Hospital son operables; la búsqueda de pacientes no | `HospitalConnectionTests`; barrido de AC 6 |
| 13 | Cada acto (incluida la comprobación de la conexión, `hospital.checked`) escribe `Audit` con `TenantId` = recepción objetivo y `Actor` que identifica plataforma y `sub` (`platform:<sub>`), sin migración | Prueba nueva |
| 14 | Activar el agente no es operable por plataforma | Caso en el barrido |
| 15 | Las vistas de conexión y WhatsApp funcionan con la sesión de plataforma y muestran el aviso | Prueba de la vista; recorrido en vivo |
| 16 | Lo que falta en la instalación (clave Kapso, webhook) se nombra como tarea del operador, sin botón que falle | `InstallationTests` |

## Anti-criterios

Mapear plataforma a `admin`; insertar un `Member` para el dueño; crear el `Tenant` al elegir; honrar la cabecera para un token de hospital; reenviar el token del dueño a Hospital para rutas clínicas; aceptar un `customer_id` o un número del llamador; activar el agente o el envío automático en nombre; abrir la bandeja «para comprobar que llegan mensajes»; una migración.

## Preguntas abiertas

- **Asumida**: el espacio lo sigue abriendo el Administrador del hospital o `BOOTSTRAP_TENANT_ID`; el dueño no lo abre.
- **Asumida**: las llamadas a Hospital en nombre usan la cuenta `recepcion-service-<tenant>`, como hoy.
- **Asumida**: el enlace de conexión de WhatsApp lo abre el dueño del número de Meta en el hospital; el dueño de plataforma lo genera y sincroniza.
- **Asumida**: «configurar el teléfono» es WhatsApp/Kapso; el teléfono de emergencia del inquilino queda fuera.
