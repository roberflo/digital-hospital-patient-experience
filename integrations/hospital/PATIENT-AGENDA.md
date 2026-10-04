# Consulta de citas por paciente

Parte de Hospital `main`, servido por el stack normal de Hospital. Publicado en Hospital `main`, commit `ad441e265c9ec268c9659bca5bf4630bb39a4c48`, desde una copia aislada de origin/main que preservó el índice compartido. Recepción integra el cambio en `6fcfa44610c23a9d2b07b195ce38f5faf2a9be05`.

- `GET /v1/agenda/patients/{patientId}?from=YYYY-MM-DD&to=YYYY-MM-DD`
- Hasta 31 días, incluye canceladas. Proyección: appointmentId, clinicianId, clinicianName, scheduledStart (offset hospital), durationMinutes, status.
- Query paciente-scoped por el pipeline de autorización/auditoría; filtro de tenant/paciente/intervalo dentro de EF. No devuelve otros pacientes ni contactos.
- Especificación y plan en Hospital: `docs/features/17-agenda-y-citas/reception-patient-agenda.spec.md`, `docs/plans/reception-patient-agenda.plan.md`.
- No hay migración ni modificación de las operaciones manuales existentes de creación, reprogramación y cancelación.

## Evidencia ejecutada

Publicación final (2026-10-03): suite completa 3507 aprobadas / 2 fallos conocidos Vitals / 429 omitidas / 3938 total. Las 45 pruebas añadidas (36 bridge, 9 agenda) pasan; arquitectura 274/274 y contratos 52/52.

Application: 152/152; Endpoints: 80/80; Infrastructure: 32 aprobadas, 5 omitidas (gates live existentes); arquitectura: 13/13; fakes de Identity: 8/8; OpenAPI: 3/3; generación del cliente TypeScript: 1/1.

TDD: ruta ausente, ToString que incluía patientId y fecha extrema que desbordaba el intervalo se detectaron en rojo y se corrigieron. Revisión independiente cerró ambos hallazgos. Los cambios OpenAPI/TS añaden únicamente una ruta y dos esquemas, preservando diferencias ajenas.

El 2026-10-03 se probó el CRUD real con JWT de servicio, stores PostgreSQL existentes y paciente sintético C: creación, lectura, reprogramación y cancelación desde Recepción. Cada fase se contrastó con la nueva API y la API original Hospital. El ciclo se repitió satisfactoriamente sobre la imagen final corregida (manifest `sha256:2286e7716f97c39e5b5c9343f77a5552a0c415d285d8a21dddbfbc1788048c36`). También pasó el rechazo de otro tenant desde CRM (404), rango mayor de 31 días (400) e historial CRM de las tres acciones. La comprobación directa de filas de auditoría Hospital y los gates completos de aislamiento siguen siendo evidencia distinta; no se infieren de este smoke.

## Stack local

La ruta se sirve desde `hospital-api-1`, la API normal de Hospital; Recepción no arranca una copia propia. Recepción se une a la red `hospital` mediante `docker-compose.hospital.yml` (servicios `web` y `recepcion-api`; el alias `api` de esa red pertenece sólo a Hospital). No se exponen puertos nuevos al host.

## Conexión local autorizada

El usuario autorizó explícitamente crear el cliente Keycloak `recepcion-agenda-local-c`, limitado al rol Recepción en el tenant sintético C, y guardar su secreto en `.env` local ignorado. Se ejecutó `scripts/connect-hospital-local.py`; se validaron tenant, audiencia y ausencia de roles clínicos/administrativos. No habilita entrega de recetas ni proveedores de mensajería.

El selector de login «Hospital local · citas de prueba» está disponible en localhost:3215, con la contraseña de desarrollo configurada. Es exclusivamente Development y no reemplaza SSO de producción. La fecha de las citas probadas es 2026-10-05, zona America/El_Salvador.

Comandos de operación local después de provisionar la cuenta autorizada:

```sh
python3 tests/hospital_live.py
python3 tests/hospital_live.py --inspect 2026-10-05
```

La prueba crea una cita, la mueve y termina cancelándola; conserva el historial hospitalario. El modo `--inspect` sólo lee. La prueba visual repitió el flujo desde la pantalla, creando a las 09:00, moviendo a las 10:00 y cancelando; se verificó cada estado directamente en Hospital y tras recargar. Evidencia local: `artifacts/hospital-crud-ui.png`. Nunca se borra físicamente una cita para simular la D de CRUD.

## Límite de reserva

Hospital permite sobrecitas manuales atribuidas. Recepción comprueba disponibilidad antes de enviar y muestra solapamientos; esto no es una reserva atómica entre ambos servicios. No se hicieron cambios especulativos a esa política. El cliente conserva la clave de intento al reintentar el mismo formulario y el CRM bloquea repeticiones procesadas, pero un resultado externo incierto requiere consultar Hospital antes de otro intento.
