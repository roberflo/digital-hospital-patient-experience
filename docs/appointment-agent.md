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
- Respuesta con un toque (`Hospital/docs/features/17-agenda-y-citas/recordatorios-whatsapp.spec.md`, S1–S2). El aviso del día anterior usa `recepcion_cita_dia_anterior_v4` (`dia_anterior_v4.json`): el cuerpo con `{{hospital}}`, `{{fecha}}` y `{{hora}}`, un pie «Para dejar de recibir recordatorios escribe BAJA.» y tres botones de respuesta rápida, «Confirmar», «Cambiar fecha» y «Cancelar», con payload `ASISTO|MOVER|CANCELAR {reminderId}`: el id del envío, no de la cita (decisión del dueño I-1), así un toque sobre un recordatorio de antes de mover la cita no confirma el nuevo inicio. La bandeja muestra el toque como «Tocó: Confirmar / Cambiar fecha / Cancelar». El de una hora antes sigue en v1, sin botones. El worker valida la forma **por ventana**: la hora antes es un solo BODY; el día anterior es BODY, FOOTER opcional y exactamente esos tres QUICK_REPLY en ese orden, con «BAJA» en el cuerpo o en el pie. v1 no vale para el día anterior y v4 no vale para la hora antes.
- **Por qué v4 y no v2.** `recepcion_cita_dia_anterior_v2` (BAJA dentro del cuerpo, botones «Confirmo» / «Cambiar fecha» / «Cancelar cita») nunca quedó aprobada: Meta la rechazó con `INVALID_FORMAT`, y una v3 idéntica también; esos nombres quedaron consumidos en Meta y no se reutilizan. v4 saca la frase de BAJA al FOOTER y acorta los botones. Se registró en el número de producción «Dte Solution» (`613008901896177`) vía Kapso y está **`PENDING`** de revisión: mientras no pase a `APPROVED`, el aviso del día anterior no sale. El archivo `dia_anterior_v2.json` se reemplazó por `dia_anterior_v4.json`.
- **Antes de encender, el Administrador cambia a la plantilla con botones** («Usar plantilla con botones» en Agente de atención, que guarda v4). Los hospitales existentes conservan en la base de datos lo que tenían (v1, o v2 si ya la eligieron; no hay `UPDATE` ni migración): mientras no cambien a v4 y v4 no esté `APPROVED`, el aviso del día anterior no sale, ni v1 en su lugar. Los hospitales nuevos nacen con v4.
- «Confirmar» (`ASISTO`) se procesa en el trabajo del agente, antes de sus puertas, esté la conversación con el agente o con una persona: sólo para ese recordatorio `sent` del día anterior, de ese contacto y su paciente, con inicio futuro y releído en Hospital como `booked` a esa hora. Queda `confirmed` y una actividad. Si no se cumple, actividad de rechazo; si Hospital no responde, «Confirmación recibida, sin verificar con Hospital». Nunca un error al paciente. El agradecimiento sólo lo envía el agente activo con `SEND_ENABLED`. La confirmación vive sólo en Recepción; Hospital no la ve.
- «Cambiar fecha» y «Cancelar» siguen la propuesta con código del agente. Si la conversación la atiende una persona, sólo dejan una actividad para el mostrador.
- «Recordatorios de mañana» (Agente de atención, Administrador y Recepción, nunca Médicos) separa Confirmaron, Sin respuesta y Revisar, con hora, nombre y estado. «Sin respuesta» no es «no vendrá».
- **Hueco pendiente antes de encender (S4):** la forma en que Kapso reenvía el toque de un botón de plantilla no se ha capturado. La prueba del criterio 5 (`AgentInteractionTests.TemplateButtonReadsAsTheCommandOrItsText`) usa un fixture sintético con la forma de WhatsApp Cloud API (`type:"button"`, `button.payload`). Con v4 `APPROVED`, un envío a un teléfono de prueba y un toque, se guarda el cuerpo recibido (sin teléfono real) como fixture y se corrige la prueba si difiere. Hasta entonces ese criterio está verde contra un supuesto.
- La cola persiste en PostgreSQL y se consulta cada minuto. La instalación y su conexión con Hospital/Kapso deben permanecer encendidas. El túnel de prueba no sustituye una URL pública estable para producción.
- `scripts/connect-hospital-local.py --delivery` prepara únicamente la capacidad explícita de la clínica sintética C; guarda secretos en archivos ignorados. `tests/hospital_delivery_live.py` verifica listado, propietario, teléfono y PDF sin WhatsApp.
- Las conversaciones que ya atiende una persona no se devuelven automáticamente al agente al activar el hospital; el equipo puede hacerlo desde la bandeja.

## Verificación local · 2026-10-03

- Backend PostgreSQL: 116/116. Regresión de interrupción: primero falló `Expected uncertain / Actual sending`; corregida la recuperación del mensaje junto a la cola, verde sin reenvío.
- Navegador: 64 casos escritorio/móvil; 63 pasaron en la ejecución completa y el caso de paginación de actividad pasó al repetirlo después del reinicio de API que coincidió con su login. Incluye los 4 casos nuevos de recordatorios.
- API smoke: 98; pruebas servidor frontend: 12; typecheck, lint y formato correctos.
- Hospital real, clínica sintética C: creación, lectura, reprogramación y cancelación comprobadas; receta firmada y PDF verificados con el servicio restringido. No se enviaron mensajes de prueba por WhatsApp.
- NVIDIA NIM: autenticación y llamada de herramienta con datos sintéticos verificadas.
- Clínica C: agente y recordatorios activados tras los checks. Las conversaciones humanas conservan su asignación. Las dos plantillas siguen `PENDING` en Meta al cierre de esta comprobación; no se despacharán mientras no estén aprobadas. Los demás hospitales siguen desactivados.
