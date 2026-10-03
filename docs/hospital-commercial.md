# CRM comercial conectado a Hospital

## Criterios y verificación

1. Hospital es dueño de empresas, convenio porcentual único y catálogo de consultas/tratamientos. Recepción consulta API; no permite editar maestros locales. Comprobar API y vista de empresas con Hospital desconectado y conectado.
2. Contacto pasa a cliente por compra completada confirmada o vínculo explícito verificado con cliente Hospital. Marcar oportunidad ganada/editar ficha no convierte. Comprobar teléfono, tenant, duplicados y permisos.
3. Un cliente tiene como máximo una empresa, determinada por Hospital; las oportunidades cotizan precios y descuento en Hospital y conservan snapshot. Comprobar manipulación del importe, cambio de tarifa y moneda.
4. Registrar compra pagada usa clave idempotente estable de oportunidad, confirmado explícitamente por una persona. Reintentar tras fallo no duplica compra ni actividad. Comprobar respuesta perdida y conflicto de cotización.
5. Compra externa Hospital se concilia por cliente ya vinculado, referencia externa exacta o paciente ya verificado, con evidencia de compra. No elegir entre teléfonos compartidos ambiguos. Comprobar primera compra y reconexión sin duplicados.
6. UI de contactos y conversación distingue contacto/cliente, muestra empresa y compras, permite vínculo y consulta. Oportunidades permite cotizar, registrar compra ya pagada y consultar historial Hospital. Comprobar móvil/escritorio.

## Invariantes

- Ningún ID enviado por navegador elige tenant, URL o credencial Hospital.
- Hospital calcula todo importe comercial. El precio de una compra completada nunca se recalcula.
- La empresa comercial no se edita como un duplicado local.
- Cotización y oportunidad ganada no prueban pago.
- Cada cliente Hospital se vincula a un solo contacto del mismo tenant; referencias externas y teléfonos se verifican antes de convertir.
- Nunca crear expediente clínico ficticio para una compra.

## Decisiones

Descuento porcentual general confirmado por el usuario. Compra significa registro manual de un pago ya recibido; no se integra pasarela ni facturación fiscal. Moneda inicial USD. Convenios y servicios se desactivan conservando historial. Hospital protege su propia autorización/auditoría; Recepción registra quién vinculó, cotizó o completó el seguimiento.

## Plan

- Implementar maestros y compras en checkout aislado de Hospital, con spec/contrato en docs/features/18-comercial y pruebas de dominio/API.
- Adaptador y persistencia de referencias comerciales en Recepción; conciliación segura y APIs de oportunidades.
- UI Hospital para gestión, UI Recepción para consulta/vínculo/cotización/registro de pago ya recibido.
- Pruebas PostgreSQL, proveedores simulados, contrato y recorrido real entre ambas aplicaciones; publicación separada por repositorio.

## Evidencia ejecutada (2026-10-03)

- Recepción: 144 pruebas backend con PostgreSQL, 102 verificaciones API, 72 pruebas navegador escritorio/móvil, 12 servidor frontend; tipos, lint, formato y compilación de contenedores aprobados.
- Hospital: 11 pruebas PostgreSQL comercial, 274 arquitectura, 52 contrato; frontend 4349 pruebas y 20 regresiones finales de recuperación de compras. Suite backend completa: 3520 pasan, 430 omitidas y 2 fallos Vitals ya diferidos por AGENTS; no se han ocultado ni atribuido a este cambio.
- `tests/hospital_commercial_live.py`: crea empresa, convenio15%, consulta100; cotiza, rechaza precio cambiado, recotiza2×120=240−15%=204; registra compra una sola vez, verifica cliente sin expediente ficticio, elimina empresa del cliente conservando snapshot, confirma aislamiento y vínculo de expediente. Maestros sintéticos desactivados después; compra y auditoría sintéticas conservadas.
- `tests/hospital_live.py`: alta, lectura, reprogramación y cancelación reales desde Recepción; comprueba persistencia también desde la API Hospital original. Cita de prueba cancelada al terminar.
- `tests/hospital_connection_live.cjs`: login administrador real, conexión verificada guardada, búsqueda real, destino no autorizado rechazado y conexión anterior conservada. `tests/hospital_calendar_live.cjs`: agenda200 y hospital de prueba recordado.
- Revisión adversarial cerró las condiciones de concurrencia de teléfono/contacto y conservación de clave idempotente después de respuesta perdida seguida de401/403.

El entorno local usa `scripts/start-hospital-commercial-local.py` y las imágenes `hospital/api:commercial`, `hospital/web:commercial`. Preserva los servicios Hospital originales y ofrece la nueva interfaz en `http://localhost:3210/es/commercial`. Aplicar primero `infra/provision/commercial.sql` y la migración del contexto Commercial, como documenta Hospital. No volver a arrancar la imagen anterior con `start-hospital-local.py`, pues no contiene las APIs comerciales.

Para el despliegue real, seguir [hospital-easypanel.md](hospital-easypanel.md). No se desplegó en un Easypanel remoto ni se enviaron mensajes WhatsApp durante estas pruebas.
