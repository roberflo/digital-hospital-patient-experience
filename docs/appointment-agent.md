# Atención de citas y recordatorios WhatsApp

## Criterios

1. El agente consulta disponibilidad y citas del paciente vinculado; propone crear, reprogramar o cancelar y exige confirmación vigente. Prueba: integración con Hospital simulado, aislamiento e idempotencia.
2. «Última receta» selecciona la firma más reciente entre todas las páginas de recetas emitidas del paciente, verificando propietario y teléfono antes del PDF. Nunca se cambia la prescripción. Prueba: páginas desordenadas, firma, propietario y entrega simulada.
3. Dos recordatorios: 09:00 del día anterior en la zona del hospital y 1 hora antes. Prueba: medianoche, cambios de horario y límites de envío.
4. Cola persistente por hospital, cita, inicio y ventana; volver a consultar Hospital antes de enviar. Cancelaciones/reprogramaciones invalidan pendientes, incluso si se hicieron en Hospital. Prueba: cambios, reinicios, carreras y duplicados.
5. Sólo pacientes vinculados que autorizaron recordatorios, canal activo y plantillas UTILITY aprobadas. BAJA revoca automáticamente; ACTIVAR RECORDATORIOS registra autorización del paciente. Prueba: revocación y bloqueos sin llamadas de envío.
6. Envío de plantilla fuera de la ventana de 24h, sin datos clínicos en la notificación; registrar estado en CRM. Entrega incierta requiere revisión, nunca reenvío automático. Prueba: payload y timeouts.
7. Administración puede configurar/pausar y revisar cola/plantillas; personal puede registrar autorización del paciente desde su conversación. Prueba: roles, aislamiento y navegador móvil/escritorio.

## Decisiones

- Horario elegido por el usuario: 09:00 del día anterior. Cancelar conserva el historial de Hospital.
- Autorizado activar en clínica C después de pruebas. Sólo plantillas aprobadas se pueden despachar.
- El recordatorio WhatsApp se gestiona en Recepción; el módulo de comunicaciones de Hospital conserva sus avisos de correo, que son otro canal.
- Las API actuales de Hospital son suficientes; no se modifica su contrato ni permisos clínicos.
- No inferir autorización del paciente por tener un teléfono. Recepción registra su autorización o el paciente escribe ACTIVAR RECORDATORIOS.
- Son defectos: enviar sin autorización, usar texto libre fuera de ventana, inventar éxito, divulgar expedientes en plantillas, repetir operaciones inciertas o usar una receta de otro paciente.
- Una interrupción que sobrepase 15 minutos del vencimiento omite el aviso y lo muestra como tal. Una reprogramación genera las ventanas del nuevo horario, sin reabrir las enviadas del mismo inicio.

## Plan

1. Completar herramientas del agente y seleccionar última receta en servidor.
2. Persistencia, cálculo temporal y sincronización/entrega de recordatorios.
3. Plantillas y configuración, autorización del paciente y visibilidad operativa.
4. Pruebas con proveedores simulados y comprobación local de Hospital; publicar y activar sólo lo aprobado.

## Operación

- `SEND_ENABLED=true` permite las respuestas automáticas; cada hospital y conversación conservan sus propios controles. `REMINDERS_SEND_ENABLED=true` controla exclusivamente notificaciones por plantilla.
- En Agente de atención, un administrador elige un número general activo y activa o pausa los recordatorios. «Consultar agenda ahora» sincroniza sin despachar; el worker realiza el envío cuando vence el aviso.
- Plantillas versionadas en `integrations/kapso/templates`. El worker exige `APPROVED`, categoría `UTILITY` y parámetros compatibles antes de cada envío; una aprobación pendiente nunca se trata como éxito.
- La cola persiste en PostgreSQL y se consulta cada minuto. La instalación y su conexión con Hospital/Kapso deben permanecer encendidas. El túnel de prueba no sustituye una URL pública estable para producción.
- `scripts/connect-hospital-delivery-local.py` prepara únicamente la capacidad explícita de la clínica sintética C; guarda secretos en archivos ignorados. `tests/hospital_delivery_live.py` verifica listado, propietario, teléfono y PDF sin WhatsApp.
- Las conversaciones que ya atiende una persona no se devuelven automáticamente al agente al activar el hospital; el equipo puede hacerlo desde la bandeja.

## Verificación local · 2026-10-03

- Backend PostgreSQL: 116/116. Regresión de interrupción: primero falló `Expected uncertain / Actual sending`; corregida la recuperación del mensaje junto a la cola, verde sin reenvío.
- Navegador: 64 casos escritorio/móvil; 63 pasaron en la ejecución completa y el caso de paginación de actividad pasó al repetirlo después del reinicio de API que coincidió con su login. Incluye los 4 casos nuevos de recordatorios.
- API smoke: 98; pruebas servidor frontend: 12; typecheck, lint y formato correctos.
- Hospital real, clínica sintética C: creación, lectura, reprogramación y cancelación comprobadas; receta firmada y PDF verificados con el servicio restringido. No se enviaron mensajes de prueba por WhatsApp.
- NVIDIA NIM: autenticación y llamada de herramienta con datos sintéticos verificadas.
- Clínica C: agente y recordatorios activados tras los checks. Las conversaciones humanas conservan su asignación. Las dos plantillas siguen `PENDING` en Meta al cierre de esta comprobación; no se despacharán mientras no estén aprobadas. Los demás hospitales siguen desactivados.
