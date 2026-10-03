# Evidencia de verificación

Fecha de desarrollo: 2026-10-02 (America/El_Salvador). Ejecución local con Docker Desktop; PostgreSQL 17, API .NET 10 y frontend Next.js 16 compilado en Node 22 LTS. Todas las pruebas usan datos sintéticos.

| Capa | Resultado ejecutado | Alcance |
|---|---|---|
| Build API | pasó | `docker compose build api`, publicación Release .NET 10 |
| Build frontend | pasó | `docker compose build web`, Next standalone + TypeScript |
| Backend | 42 pasaron, 0 fallos, 0 omitidas | `scripts/test-backend.sh`, PostgreSQL temporal real y proveedores simulados |
| API | 65 verificaciones pasaron | `python3 tests/api_smoke.py`, API real local |
| Navegador | 10 pasaron | `npm test --prefix frontend`, Chromium escritorio + móvil contra contenedores compilados |
| Adaptador Hospital | pasó | `tests/hospital-client/HospitalClient.Checks.csproj` ejecutado con SDK 10 |
| Bridge Hospital | 33 servicio/HTTP-JWT + 12 arquitectura + 3 OpenAPI + 1 cliente TS pasaron | copia aislada del hospital; detalles en `integrations/hospital/README.md` |
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

No se ha validado SSO contra un Keycloak real, Google OAuth real, entrega real de WhatsApp, coexistencia real ni agenda/recetas contra stores del Hospital desplegado. El número de Kapso verificado es sandbox sin coexistencia. El bridge Hospital usa fixtures de repositorios y renderer en sus pruebas dirigidas; no se ejecutó la suite completa de Hospital ni se sustituyó la revisión de sus CODEOWNERS.

No se han realizado pruebas de carga, recuperación total de un host perdido, evaluación clínica formal del modelo ni auditoría externa de seguridad. La restauración probada verifica PostgreSQL; la disponibilidad conjunta del keyring y las variables estables debe ensayarse en el entorno de operación. El pipeline GitHub Actions reproduce build, API, backend y navegador con credenciales sintéticas; su estado remoto se consulta en Actions y no se infiere de estos resultados locales.

La entrega es una aplicación ejecutable con verificaciones concretas. Los adaptadores probados y los documentos de despliegue no se presentan como una instalación productiva ya habilitada.

## Bandeja y CRM: ampliación de atención

- Estados abierta/pendiente/pospuesta/resuelta, prioridad, etiquetas, filtros por canal y responsable, lecturas por usuario y paginación. Asignar a un humano conserva el estado pendiente; habilitar al agente reabre explícitamente.
- Una respuesta entrante reabre la conversación y conserva la atención humana. Las conversaciones pospuestas vencidas se reabren una sola vez, sin enviar mensajes automáticos.
- Ficha CRM compartida por contacto, empresa/convenio, ciclo del cliente, seguimientos vinculados a la conversación y cronología conjunta; referencias a otro paciente o tenant rechazadas.
- Respuestas guardadas por negocio, administradas por supervisores. Insertarlas en el editor no envía un mensaje.
- Dashboard separado y bandeja ajustada a la ventana; las pruebas verifican ausencia de tarjetas de métricas y posición/altura en escritorio y móvil.
- Diagnóstico de canal restringido a administradores; muestra salud del proveedor, webhook y firma sin devolver claves ni encabezados privados.

### Resultado de la búsqueda de números Kapso

Las dos claves facilitadas permiten consultar el mismo proyecto `carsales`. La lista completa accesible contiene únicamente `Sandbox WhatsApp`, ID `597907523413541`; no reporta coexistencia, no tiene webhooks y su comprobación de salud devuelve `unhealthy`. No hay un número operativo verificable con estos accesos. No se enviaron mensajes externos. Se necesita acceso al proyecto de los números reales y la URL HTTPS pública de esta API para completar la prueba de recepción y entrega real.
