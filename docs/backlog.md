# Recepción — plan y backlog verificable

Fecha: 2026-10-02. Alcance: CRM agent-first para un hospital por empresa, usuarios compartidos con Hospital, WhatsApp general y por doctor, expediente/recetas mediante API y calendario del hospital.

## Invariantes

- INV-1: cada usuario pertenece a un solo tenant; el tenant nunca se toma del texto del agente ni de un encabezado del navegador.
- INV-2: un canal pertenece a un tenant; webhooks firmados resuelven el tenant por el número configurado.
- INV-3: únicamente un teléfono vinculado inequívocamente por Hospital puede consultar sus recetas. Un contacto CRM no prueba identidad clínica.
- INV-4: el agente no prescribe ni altera tratamientos. Solo entrega documentos emitidos y explica instrucciones registradas; dudas clínicas se transfieren.
- INV-5: una conversación transferida pausa al agente; las acciones y sus resultados quedan en historial.
- INV-6: mensajes duplicados y reintentos no generan respuestas/citas duplicadas. En resultados externos inciertos se requiere conciliación, no reenvío ciego.
- INV-7: agenda de Hospital es autoridad. Google recibe eventos sin información clínica; un calendario por hospital.
- INV-8: secretos fuera del repositorio/frontend/logs; fixtures sintéticos en pruebas.

## Backlog

| ID | Entrega / criterio de aceptación | Cómo se prueba | Estado |
|---|---|---|---|
| B01 | Stack reproducible Next.js/shadcn, .NET 10, PostgreSQL; health/readiness | builds y compose healthy | Implementado; compose/build local verificado |
| B02 | Keycloak compartido, roles y membresía única; demo solo Development explícito | 401/403, token inválido, acceso cruzado entre dos tenants | Implementado y probado; SSO real pendiente |
| B03 | Contactos, oportunidades, actividades y notas persistentes (las empresas viven en Hospital desde 2026-10-25) | CRUD API + UI, validación y aislamiento | Implementado y probado |
| B04 | Bandeja de conversaciones, asignación, historial, pausa/reanudación del agente | webhook → bandeja → humano y autorización | Implementado y probado |
| B05 | Kapso: números, onboarding, firma, deduplicación, media, coexistencia | fixtures oficiales, firma inválida, eventos repetidos y ecos | Implementado y probado con fixtures; MCP real verificado; coexistencia cubierta por pruebas (`d098e19`), real pendiente |
| B06 | Agente intercambiable (pasarela NIM → OpenAI → Claude, `AgentGateway.cs`) con herramientas acotadas y trazabilidad | proveedor simulado, tool calls y handoff | Implementado; mocks y NIM real con datos sintéticos verificados |
| B07 | Integración API Hospital para identidad, agenda y recetas | contratos reales, errores y falta de vínculo | Implementado; adaptador y bridge probados, smoke Hospital real pendiente |
| B08 | Google OAuth y sincronización de agenda por hospital | estado OAuth, tokens protegidos, upsert/cancel idempotente | Implementado y probado con proveedor simulado; OAuth real pendiente |
| B09 | Guía por empresa y administración de canales/usuarios | restricciones admin y separación por tenant | Implementado y probado |
| B10 | Contenedores Easypanel, secretos, backups, despliegue documentado | configuración validada y smoke tests | Contenedores y guía listos; despliegue Easypanel pendiente |
| B11 | Pruebas de seguridad, integración y navegador; revisión | resultados reales registrados, sin simular pases | Pruebas dirigidas y E2E locales verificadas; ver verification.md |

## Ampliación de bandeja

| ID | Entrega | Estado |
|---|---|---|
| B12 | Dashboard separado; bandeja compacta y adaptada al alto disponible | Implementado; verificado escritorio/móvil |
| B13 | Estados, prioridad, etiquetas, responsable, lecturas y respuestas guardadas | Implementado; API, worker y UI probados |
| B14 | Ficha CRM, empresa (leída de Hospital desde `cc9ab0e`, 2026-10-25), ciclo del cliente, seguimientos e historial desde el chat | Implementado; aislamiento y persistencia verificados |
| B15 | Diagnóstico real de Kapso y vinculación del webhook | Diagnóstico implementado; único número accesible sandbox no operativo, falta acceso al número real y URL pública |

| B16 | Vistas personales, macros auditadas, búsqueda de mensajes y filtro de archivos | Implementado y probado; ver chatwoot-review.md |

| B17 | Consulta de citas por paciente y conexión de agenda Hospital | Completado localmente: CRUD real API/UI contra Hospital; publicado en ambos repositorios el 2026-10-03 |

## Secuencia

1. Inspeccionar contratos de Hospital y proveedores, establecer riesgos y decisiones.
2. Construir persistencia, identidad y API; verificar aislamiento antes de conectar agentes.
3. Implementar CRM y bandeja, worker durable, Kapso y agente con herramientas limitadas.
4. Integrar agenda/recetas reales y Google; verificar transferencia, archivos y estados.
5. Completar UI, pruebas, despliegue reproducible y guía operativa.

## Anti-criterios

No considerar listo un mock; no confiar en tenant enviado por cliente; no enviar recetas por coincidencias ambiguas; no exponer claves; no activar respuestas reales durante pruebas; no modificar dosis; no reintentar automáticamente una entrega de resultado incierto; no declarar pruebas no ejecutadas.

## Dependencias y supuestos

- Asumida: sincronización inicial Hospital → Google. Google no crea ni cambia citas del hospital.
- Asumida: números de doctores usan coexistencia cuando Kapso/Meta lo permiten; ecos salientes transfieren al humano.
- Bloqueante para validar Google real: OAuth client/secret, redirect URI y calendario de prueba.
- Bloqueante para producción: dominios HTTPS, servicio de identidad y credenciales Hospital con permisos acotados por tenant.
- Asumida: pruebas sintéticas y modo de envío desactivado hasta configuración explícita del negocio.
- Las claves recibidas se usan solo como secretos locales ignorados y configuración de despliegue; no se incluyen en documentos.

## Puesta en operación pendiente de configuración externa

- R01: dominios y acceso al proyecto Easypanel; desplegar imágenes y volúmenes.
- R02: cliente web Keycloak, tenant real y cuentas de servicio por Hospital; validar login/refresh real.
- R03: desplegar el bridge aplicado en Hospital y probar receta/agenda con stores reales.
- R04: OAuth Google, calendario compartido y consentimiento real; probar alta, cambio y cancelación.
- R05: números WhatsApp de negocio/doctores elegibles para coexistencia, webhook y prueba controlada.

Estas tareas requieren datos/cuentas del entorno objetivo y no se marcan como completadas por haber implementado sus adaptadores.

## Revisión «lista para cliente» (2026-10-20)

Revisión de QA, UX y arquitectura sobre `d4dce0c`; lo corregido está en la rama `qa/recepcion-listo-cliente`. Queda abierto:

- R06 (antes del primer paciente): respaldo nocturno programado fuera del host y una restauración ensayada en el Easypanel real; monitores externos sobre `/health/ready` y `/health/attention` ([despliegue](deployment.md)).
- R07 (antes de `SEND_ENABLED`): contrato y acuerdo de tratamiento de datos de salud con NVIDIA (y OpenAI o Anthropic si la pasarela tiene `OPENAI_API_KEY`/`ANTHROPIC_API_KEY`), o endpoint dedicado.
- A01: mensajes perdidos durante una caída de la API más larga que los reintentos de Kapso no se recuperan; un job por canal que pida a Kapso los mensajes desde `LastWebhookAt`.
- A02: la última receta firmada se busca con una petición por receta (`HospitalClient`); el bridge de Hospital debería devolver `signedAt` en la lista.
- A03 (a escala): el worker lista todos los hospitales cada 1,5 s; listar sólo los que tienen trabajo o tareas vencidas. Estado en memoria (renovación de sesión, deduplicación del número general, retención) impide más de una réplica.
- A04 (a escala): Row-Level Security en PostgreSQL como defensa en profundidad del filtro por tenant, y retirar el secreto compartido de cuentas de servicio cuando todas usen client-jwt.
- A05 (decisión de diseño): en el registro de un paciente nuevo con hora, el nombre del doctor de la tarjeta y del historial sale del texto del toque (el doctor y la hora sí los valida Hospital al confirmar); nombrarlo desde Hospital exige guardar los horarios ofrecidos o una lectura más antes de Confirmar, que hoy ese camino evita a propósito.
