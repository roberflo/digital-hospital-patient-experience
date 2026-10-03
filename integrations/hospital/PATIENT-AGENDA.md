# Consulta de citas por paciente

Cambio aplicado en el checkout Hospital y preservado en `patient-agenda.patch`. No se hizo commit en el índice compartido de Hospital.

- `GET /v1/agenda/patients/{patientId}?from=YYYY-MM-DD&to=YYYY-MM-DD`
- Hasta 31 días, incluye canceladas. Proyección: appointmentId, clinicianId, clinicianName, scheduledStart (offset hospital), durationMinutes, status.
- Query paciente-scoped por el pipeline de autorización/auditoría; filtro de tenant/paciente/intervalo dentro de EF. No devuelve otros pacientes ni contactos.
- Especificación y plan en Hospital: `docs/features/17-agenda-y-citas/reception-patient-agenda.spec.md`, `docs/plans/reception-patient-agenda.plan.md`.
- No hay migración ni modificación de las operaciones manuales existentes de creación, reprogramación y cancelación.

## Evidencia ejecutada

Application: 152/152; Endpoints: 80/80; Infrastructure: 32 aprobadas, 5 omitidas (gates live existentes); arquitectura: 13/13; fakes de Identity: 8/8; OpenAPI: 3/3; generación del cliente TypeScript: 1/1.

TDD: ruta ausente, ToString que incluía patientId y fecha extrema que desbordaba el intervalo se detectaron en rojo y se corrigieron. Revisión independiente cerró ambos hallazgos. Los cambios OpenAPI/TS añaden únicamente una ruta y dos esquemas, preservando diferencias ajenas.

Pruebas HTTP con JWT real, aislamiento en PostgreSQL y auditoría persistida de esta nueva ruta **pendientes**; los tests InMemory no las reemplazan. No se ejecutó la suite completa de Hospital.

## Imagen local preparada

`hospital/api:recepcion-patient-agenda` compiló correctamente. No está desplegada. El contenedor Hospital actual proviene de un checkout temporal que ya no existe; por ello se preparó `patient-agenda.compose.yml` para una API adicional sin reemplazar la activa. La API adicional usa exclusivamente el entorno de runtime de la API existente; nunca todo el `.env` de Hospital ni credenciales de migración.

Recepción se une únicamente a la red `hospital` mediante `docker-compose.hospital.yml`. Sólo la API Hospital adicional requiere su red de llaves habitual. No se exponen puertos nuevos al host.

## Conexión pendiente de autorización

La revisión automática requirió autorización específica antes de crear el cliente Keycloak persistente `recepcion-agenda-local-c`, limitado al rol Recepción en el tenant sintético C. `scripts/connect-hospital-local.py` está preparado y su sintaxis fue validada, pero **no se ejecutó**. No se guardaron credenciales ni se habilitó el usuario de integración en la UI.

Después de autorizar: provisionar cliente, preparar el env privado de runtime para la API adicional, iniciarla, conectar Recepción con el override y probar consultar → crear → reprogramar → cancelar con un paciente sintético. El selector de login «Hospital local · citas de prueba» sólo aparece cuando se configura `DEV_HOSPITAL_TENANT_ID`; es exclusivamente Development y no reemplaza SSO de producción.

La autorización del usuario debe mantenerse como condición; no sustituirla usando tokens de otra cuenta para activar la conexión.

## Límite de reserva

Hospital permite sobrecitas manuales atribuidas. Recepción comprueba disponibilidad antes de enviar y muestra solapamientos; esto no es una reserva atómica entre ambos servicios. No se hicieron cambios especulativos a esa política. El cliente conserva la clave de intento al reintentar el mismo formulario y el CRM bloquea repeticiones procesadas, pero un resultado externo incierto requiere consultar Hospital antes de otro intento.
