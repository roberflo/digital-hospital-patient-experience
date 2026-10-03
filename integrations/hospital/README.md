# Puente de recetas para recepción — patch revisable

Estado: **aplicado al hospital original el 2026-10-02; compila y pasa pruebas dirigidas en copia aislada**. `reception-delivery.patch` añade contrato compartido, controller en ClinicalRecord.Endpoints, servicio en Bootstrap, pruebas dirigidas, links a fixtures sintéticos existentes, spec/plan, OpenAPI, cliente TypeScript generado y registro scoped en `Hospital.Api`. `git apply --reverse --check` confirma la aplicación completa; el patch se conserva para revisión, no debe aplicarse dos veces. El patch contiene los fuentes y pruebas; una copia local equivalente está en `staged/` (ignorada por Git). No hay secretos, migraciones ni cambios en roles clínicos existentes.

El bootstrap reúne puertos ya publicados de Patient, ClinicalRecord, Timeline, Audit, Calendar y PDF. El servicio nuevo realiza autorización y auditoría en cada entrada. No reusa un usuario médico, no debilita `PatientResourceClassPolicy`, y no llama a las rutas generales del expediente con privilegios prestados. El controller referencia el contrato compartido; no se añade dependencia de Endpoints hacia Bootstrap. Las pruebas del grafo de referencias pasan.

## Criterios de aceptación

| AC | Criterio verificable | Cómo se prueba |
|---|---|---|
| 1 | JWT válido, `tenant_id` coincidente, `azp` exacto, `sub` exacto de cuenta de servicio y role `reception-agent`; integración habilitada explícitamente por tenant | Test HTTP con token de servicio y rechazos para rol solo, client ajeno, sujeto humano, tenant ajeno, flag ausente y anónimo |
| 2 | Paciente de ese tenant, expediente activo y teléfono completo actual coincidente antes de leer receta | Fake `IPatientRepository` + DB real cross tenant; asserts de que no se consulta receta ante mismatch |
| 3 | Lista únicamente IDs de recetas firmadas; paginación conserva cursor | Fake contribuidor específico Prescription, recetas canceladas/void omitidas, página siguiente; ningún contribuidor de notas/observaciones invocado |
| 4 | Detalle y PDF de receta cuyo patientId coincide y estado actual signed | `IPrescriptionRepository.FindPatientIdAsync` antes de decrypt; foreign patient/tenant/draft/cancelled/void devuelve mismo 404 |
| 5 | PDF usa mapper/renderer hospitalarios, licencia congelada y perfil del firmante | Golden PDF existente y caso licencia rechazada/perfil faltante; nunca generar receta nueva |
| 6 | AuditActor.Service para cuenta real; no se afirma una persona como autora | Audit store real con actor service y cuenta indicada; intentos humanos denegados como User |
| 7 | Auditoría awaited antes de retornar contenido; fallo del sink impide devolver receta | Sink que falla; HTTP no devuelve contenido; conteo exacto allowed/denied y hash sobre bytes entregados |
| 8 | Ningún teléfono/medicamento en ruta, mensajes de error, ToString ni auditoría | Captura de logs con fixtures sintéticos y pruebas de ausencia |
| 9 | Rutas clínicas generales conservan sus permisos; service sin rol clínico no obtiene notas, alergias, observaciones ni firma | Tests HTTP negativos sobre rutas existentes; suite de arquitectura y autorización sin relajar assertions |

Anti-criterios: asignar `Médicos` al bot; permitir `patientId` ajeno por solo conocer UUID; listas completas de expediente al LLM; usar último dígitos del teléfono; devolver canceladas o entered-in-error; registrar PHI en auditoría; habilitar bridge antes de probarlo; declarar la suite verde sin ejecutarla.

INV-1: una entrega pertenece a exactamente un tenant, paciente vinculado, receta emitida y cuenta de servicio.
INV-2: el agente jamás produce una prescripción ni altera indicaciones.
INV-3: ninguna entrega de contenido ocurre sin completar auditoría.
INV-4: permisos clínicos ordinarios permanecen intactos.

Asumida: el propietario autoriza entregar recetas existentes al teléfono vinculado (confirmado en este chat); no autoriza acceso por familiares ni cambiar dosis. Asumida: el endpoint hospitalario es interno y protegido por JWT, además de las comprobaciones por tenant. Bloqueante para habilitar producción: revisión independiente y smoke con stores reales de AC2/5/6/9; disponer del sujeto real de la cuenta de servicio. Estas credenciales externas no impiden revisar/aplicar el código dentro de un entorno de pruebas.

## Contrato

```text
POST /v1/reception/patients/{patientId}/prescriptions/list
  {"phone":"+503...","cursor":null}
  -> {"prescriptionIds":["UUID"],"nextCursor":null}
POST /v1/reception/prescriptions/{prescriptionId}
  {"phone":"+503..."}
  -> {prescriptionId,patientId,encounterId,state:"signed",signedAt,contentWithheld:false,lines:[...]}
POST /v1/reception/prescriptions/{prescriptionId}/pdf
  {"phone":"+503..."}
  -> application/pdf
```

Todos los endpoints envían `Cache-Control: no-store`. El teléfono sale de la conversación de WhatsApp verificada, no de un argumento libre del LLM. El CRM ya soporta estas rutas activando `UseReceptionBridge=true` por tenant, tras desplegar y validar el patch.

## Configuración y aplicación

Hospital:

```text
ReceptionDelivery__Tenants__<tenant-guid>__Enabled=true
ReceptionDelivery__Tenants__<tenant-guid>__ClientId=recepcion-hospital-1
ReceptionDelivery__Tenants__<tenant-guid>__ServiceAccountSubject=<keycloak-service-account-sub-guid>
```

Keycloak: cliente confidential exclusivo, service accounts activado, standard/direct access flows desactivados, mapper `tenant_id` del hospital y role `reception-agent`. Agregar `Recepción` sólo si este mismo cliente atiende las rutas de agenda/demografía existentes. Ningún rol clínico. CRM usa `ClientSecret` de ese cliente; no una clave del administrador Keycloak.

CRM:

```text
Hospital__Tenants__<tenant-guid>__AllowClinicalDelivery=true
Hospital__Tenants__<tenant-guid>__UseReceptionBridge=true
```

Comandos de referencia para otro checkout Hospital sin el bridge:

```sh
git apply --check ../Recepcion/integrations/hospital/reception-delivery.patch
git apply ../Recepcion/integrations/hospital/reception-delivery.patch
pnpm backend:test
pnpm backend:live
```

Las pruebas del servicio, HTTP/JWT, arquitectura y OpenAPI indicadas abajo ya se ejecutaron. Se aplicó sobre el checkout actual preservando cambios anteriores de terceros; el cliente generado pasó en Hospital real. Falta el smoke con stores reales y revisión independiente según REVIEW.md. La prueba `git apply --check` por sí sola no compila el código ni prueba permisos. Google Calendar es un flujo separado; este bridge no lo implementa.

## Evidencia de validación en copia aislada

Copia: `Recepcion/artifacts/hospital-validation`. Se usó el wrapper real `scripts/dotnet.sh`, SDK 10 y el cache `hospital-nuget`. Luego se aplicó exactamente el diff validado a Hospital original, sin git add ni commit.

```text
dotnet test backend/tests/Bootstrap/Hospital.Api.IntegrationTests/Hospital.Api.IntegrationTests.csproj --filter FullyQualifiedName~ReceptionDelivery
Passed! - Failed: 0, Passed: 33, Skipped: 0, Total: 33

dotnet test backend/tests/Architecture/Hospital.Architecture.Tests/Hospital.Architecture.Tests.csproj --filter 'FullyQualifiedName~ProjectReferenceGraphTests|FullyQualifiedName~NoPhiInContractDiagnosticsTests|FullyQualifiedName~PatientAccessResourceIdentityTests'
Passed! - Failed: 0, Passed: 12, Skipped: 0, Total: 12
```

Los 21 casos del servicio prueban el servicio real con puertos controlados: identidad/tenant/cliente/rol, teléfono, paciente ausente, receta ajena sin decrypt, draft/cancelled/entered-in-error, lista filtrada, PDF a través del mapper real, licencia inválida, fallo del sink y auditoría. Además hay 12 casos HTTP TestServer con validación JWT real de firma, issuer, audience y lifetime, rutas, permisos, respuestas y Cache-Control. El renderer PDF y repositorios están simulados; no equivalen a Golden PDF ni a Keycloak/PostgreSQL reales. El adaptador CRM también pasó checks en contenedor .NET 10.

Hallazgos corregidos: controller inicial en Bootstrap activaba AV0016; se movió a Endpoints sin cambiar defaults de versionado ni silenciar el analyzer. Un fixture de cancelación omitía la respuesta obligatoria sobre entrega de papel; ahora declara `PaperDeliveryState.Unknown`, sin cambiar assertions. Estas pruebas se añadieron durante implementación, por lo que no se declara una secuencia TDD roja previa al código.

OpenAPI baseline: 3/3 pasan (el primer rojo detectó exactamente las tres rutas nuevas; se regeneró con REGEN_BASELINE=true y volvió a ejecutarse sin flag). Cliente TypeScript generado: vitest 1/1 en Hospital real. Regresión realm_access raíz array: rojo 1 falla/20 pasan con InvalidOperationException; después guard ValueKind, 33/33 servicio+HTTP pasan. Logs en artifacts/hospital-validation: reception-nonobject-red.log, reception-http-tests.log, architecture-tests.log, openapi-tests.log.

Pendiente de habilitar el bridge: auditoría/postgres reales y smoke de cuenta Keycloak dedicada; Golden PDF y gates vivos pertinentes. No se ejecutó ni se afirma que los 4,131 tests completos del hospital hayan pasado. No se habilitó proveedor ni tenant productivo, no se crearon credenciales.

Revisión del autor: Bugs (realm_access no objeto corregido); Seguridad (tenant/sub/azp/rol/teléfono y estado verificados, audit awaited, sin fallback clínico); Conformidad (spec/plan agregados, sin cambios de permisos generales); Diseño (API sin UI, no aplica). La aprobación independiente corresponde al coordinador, no al autor.

## Revisión independiente del coordinador

2026-10-02: revisados los cuatro pases de REVIEW.md. Bugs: IDs de timeline coinciden con receta y permisos se comprueban antes de leer; Seguridad: caller firmado, tenant/sub/azp/role exactos, número actual, signed-only y audit awaited; Conformidad: AC1–9 reflejados, límites de evidencia indicados arriba; Diseño: endpoints sin interfaz, no aplica. No se detectaron nuevos hallazgos importantes en el patch. Esta revisión de código no reemplaza el smoke de stores reales ni la aprobación de CODEOWNERS para merge.

## Revisión de publicación, 2026-10-03

Detalle y PDF usan una única ID de receta; el servicio autentica la capacidad antes de resolver su propietario por tenant. Recepción valida el propietario del detalle contra su paciente vinculado antes de pedir PDF, incluso con teléfonos compartidos. La composición del documento se comparte con el print job canónico del Hospital. Regresiones de arquitectura detectaron el render duplicado y el binding de dos identificadores; se corrigió el código sin debilitar sus assertions.

Resultados finales: 36/36 bridge, 274/274 arquitectura, 52/52 contratos; suite completa 3507 aprobadas, 2 fallos conocidos Vitals, 429 omitidas, 3938 total (origin/main 3893 +45 pruebas propias). Revisión independiente completó bugs, seguridad, conformidad y diseño sin nuevos Important. La conexión real de agenda sí se probó en PostgreSQL local por API e interfaz; la entrega de recetas sigue deshabilitada y requiere su propia habilitación y prueba clínica.

Publicado en Hospital `main`: [ad441e2](https://github.com/roberflo/digital-hospital/commit/ad441e265c9ec268c9659bca5bf4630bb39a4c48). Los patches finales corresponden a ese commit y fueron comprobados en orden sobre origin/main anterior.
