# Evidencia de verificación

Fecha de desarrollo: 2026-10-02 (America/El_Salvador). Ejecución local con Docker Desktop; PostgreSQL 17, API .NET 10 y frontend Next.js 16 compilado en Node 22 LTS. Todas las pruebas usan datos sintéticos.

| Capa | Resultado ejecutado | Alcance |
|---|---|---|
| Build API | pasó | `docker compose build api`, publicación Release .NET 10 |
| Build frontend | pasó | `docker compose build web`, Next standalone + TypeScript |
| Backend | 56 pasaron, 0 fallos, 0 omitidas | `scripts/test-backend.sh`, PostgreSQL temporal real y proveedores simulados |
| API | 82 verificaciones pasaron | `python3 tests/api_smoke.py`, API real local |
| Navegador | 14/14 pasaron en una ejecución completa tras corregir el nombre junto al contador de no leídos | `npm test --prefix frontend`, Chromium escritorio + móvil contra contenedores compilados |
| Adaptador Hospital | pasó | `tests/hospital-client/HospitalClient.Checks.csproj` ejecutado con SDK 10 |
| Bridge Hospital | 36/36 servicio/HTTP-JWT; arquitectura 274/274; contratos 52/52 | copia aislada del hospital; revisión independiente sin hallazgos Important nuevos |
| Suite Hospital completa | 3507 aprobadas, 2 fallos conocidos Vitals, 429 omitidas; 3938 total | base origin/main 3893 + 45 pruebas propias (36 bridge y 9 agenda); no se declara toda la suite verde |
| Kapso MCP | conexión y lecturas reales correctas | initialize, tools/list y lectura del número configurado; servidor kapso-mcp 2.0.0 |
| NVIDIA NIM | modelos, tool calling y 2 comprobaciones del asistente pasaron | proveedor real, prompt y contacto sintéticos; modelo `nvidia/nemotron-3-super-120b-a12b` |
| Backup | pasó | dump + keyring generados; dump restaurado en base temporal y consulta sobre campos cifrados |
| Dependencias frontend | 0 vulnerabilidades reportadas | `npm install` / `npm ci` con audit durante el build |
| Formato / secretos | pasó | Prettier, `git diff --check` y escaneo de valores de secretos contra archivos candidatos a commit |

## Qué se comprobó

- Rechazo anónimo, roles de admin/doctor/recepción, tenant autenticado, membresía única incluso ante JWT firmado con empresa contradictoria, escrituras y referencias entre empresas rechazadas.
- Persistencia de contactos, oportunidades, etapas, notas y auditoría; estado conservado tras recargar vistas en navegador.
- Firma HMAC inválida y falsificada; reenvío del mismo webhook y del mismo mensaje con otra clave; eco Business App que pausa al agente; read receipts sobre mensajes existentes; canal pausado impide envío real.
- Agente con conversación humana no invoca NIM; evento antiguo se descarta; takeover humano o desactivación del negocio durante el modelo evita respuesta; envío idempotente y resultado incierto sin reintento; handoff registra historial y acuse.
- Cliente Kapso recupera el customer existente por tenant, o crea con external ID; pruebas sin crear clientes externos reales.
- Agenda privada verifica teléfono y filtra antes de devolver al modelo: no aparecen otros pacientes, sus nombres ni sus identificadores.
- Google usa ID determinista por tenant/cita, PUT→POST en ausencia y DELETE tolerante a evento ausente para los estados reales `cancelled-by-patient`, `cancelled-by-clinic`, `entered-in-error`. El cuerpo excluye datos de paciente.
- Login, bandeja, detalle, notas, contactos, pipeline y configuración en escritorio y móvil; no hay desbordamiento horizontal en las vistas probadas. Capturas locales en `artifacts/` (ignoradas por Git).
- Migración inicial y posterior asociación Kapso aplicadas en base local; cada ejecución backend crea una nueva base temporal y aplica migraciones desde cero.

## Lo que todavía requiere el entorno objetivo

Se validó autenticación de servicio Keycloak y CRUD de agenda contra stores reales del Hospital local sintético. No se ha validado SSO interactivo de producción, Google OAuth real, entrega real de WhatsApp, coexistencia real ni entrega de recetas contra stores reales. El número de Kapso verificado es sandbox sin coexistencia. El bridge Hospital usa fixtures de repositorios y renderer en sus pruebas dirigidas; la suite completa final de Hospital conserva los dos fallos Vitals previamente documentados; no se sustituye la revisión de sus CODEOWNERS.

No se han realizado pruebas de carga, recuperación total de un host perdido, evaluación clínica formal del modelo ni auditoría externa de seguridad. La restauración probada verifica PostgreSQL; la disponibilidad conjunta del keyring y las variables estables debe ensayarse en el entorno de operación. El pipeline GitHub Actions reproduce build, API, backend y navegador con credenciales sintéticas; su estado remoto se consulta en Actions y no se infiere de estos resultados locales.

La entrega es una aplicación ejecutable con verificaciones concretas. Los adaptadores probados y los documentos de despliegue no se presentan como una instalación productiva ya habilitada.

La ejecución remota de `27a8498` detectó ocho fallos al seleccionar a Ana con mensajes sin leer: el contador formaba parte del texto del nombre. El nombre ahora tiene su propio elemento; la prueba de bandeja fuerza el contador en la respuesta de listado para cubrir ese estado también sobre una base local ya leída. La suite local completa pasó después del cambio; las demás llamadas de esa prueba siguen usando la API real.

## Bandeja y CRM: ampliación de atención

- Estados abierta/pendiente/pospuesta/resuelta, prioridad, etiquetas, filtros por canal y responsable, lecturas por usuario y paginación. Asignar a un humano conserva el estado pendiente; habilitar al agente reabre explícitamente.
- Una respuesta entrante reabre la conversación y conserva la atención humana. Las conversaciones pospuestas vencidas se reabren una sola vez, sin enviar mensajes automáticos.
- Ficha CRM compartida por contacto, empresa/convenio, ciclo del cliente, seguimientos vinculados a la conversación y cronología conjunta; referencias a otro paciente o tenant rechazadas.
- Respuestas guardadas por negocio, administradas por supervisores. Insertarlas en el editor no envía un mensaje.
- Dashboard separado y bandeja ajustada a la ventana; las pruebas verifican ausencia de tarjetas de métricas y posición/altura en escritorio y móvil.
- Diagnóstico de canal restringido a administradores; muestra salud del proveedor, webhook y firma sin devolver claves ni encabezados privados.

### Resultado de la búsqueda de números Kapso

Las dos claves facilitadas permiten consultar el mismo proyecto `carsales`. La lista completa accesible contiene únicamente `Sandbox WhatsApp`, ID `597907523413541`; no reporta coexistencia, no tiene webhooks y su comprobación de salud devuelve `unhealthy`. No hay un número operativo verificable con estos accesos. No se enviaron mensajes externos. Se necesita acceso al proyecto de los números reales y la URL HTTPS pública de esta API para completar la prueba de recepción y entrega real.

## Agenda Hospital: alcance actual

Se agregó la consulta por paciente en Hospital (guard/auditoría, filtro tenant/paciente, rango máximo 31 días), y su consumo desde el agente y la ficha de la bandeja. Se corrigió la validación de horarios nocturnos por zona del hospital. Build y pruebas dirigidas pasan; consultar `integrations/hospital/PATIENT-AGENDA.md` para conteos y límites de la evidencia. El 2026-10-03, tras autorización explícita de la cuenta de servicio, se activó la API local adicional y se probó creación, consulta, reprogramación y cancelación real desde CRM, tanto por API como por interfaz. Cada estado se contrastó con la API original Hospital sobre PostgreSQL. El smoke reproducible es `python3 tests/hospital_live.py`; las citas sintéticas terminaron canceladas, preservando el historial.

## Ajuste del contrato de recetas (2026-10-03)

La lectura/PDF recibe únicamente el identificador de receta en la ruta. Hospital resuelve su propietario y valida el teléfono; Recepción verifica también que el propietario devuelto coincide con su contacto antes de pedir el PDF, incluso cuando dos pacientes comparten teléfono. Regresión ejecutada en rojo: `Unexpected Hospital route: /v1/reception/patients/.../prescriptions/.../pdf` (2 fallos); después de actualizar rutas y prevalidar propietario, backend 56/56. El puente de recetas permanece deshabilitado localmente y no se declara validada una entrega clínica real.
