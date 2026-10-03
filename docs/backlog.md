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
| B01 | Stack reproducible Next.js/shadcn, .NET 10, PostgreSQL; health/readiness | builds y compose healthy | pendiente |
| B02 | Keycloak compartido, roles y membresía única; demo solo Development explícito | 401/403, token inválido, acceso cruzado entre dos tenants | pendiente |
| B03 | Contactos, empresas, oportunidades, actividades y notas persistentes | CRUD API + UI, validación y aislamiento | pendiente |
| B04 | Bandeja de conversaciones, asignación, historial, pausa/reanudación del agente | webhook → bandeja → humano y autorización | pendiente |
| B05 | Kapso: números, onboarding, firma, deduplicación, media, coexistencia | fixtures oficiales, firma inválida, eventos repetidos y ecos | pendiente |
| B06 | Agente NIM intercambiable con herramientas acotadas y trazabilidad | proveedor simulado, tool calls y handoff | pendiente |
| B07 | Integración API Hospital para identidad, agenda y recetas | contratos reales, errores y falta de vínculo | pendiente |
| B08 | Google OAuth y sincronización de agenda por hospital | estado OAuth, tokens protegidos, upsert/cancel idempotente | pendiente |
| B09 | Guía por empresa y administración de canales/usuarios | restricciones admin y separación por tenant | pendiente |
| B10 | Contenedores Easypanel, secretos, backups, despliegue documentado | configuración validada y smoke tests | pendiente |
| B11 | Pruebas de seguridad, integración y navegador; revisión | resultados reales registrados, sin simular pases | pendiente |

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
