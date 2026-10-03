# Integración con Hospital

Contratos comprobados en el código de `../Hospital/backend/src` el 2026-10-02. El adaptador compilable vive en `backend/Integrations/HospitalClient.cs`. El bridge de recetas se aplicó al hospital autorizado; ver integrations/hospital/README.md para archivos y pruebas.

## Identidad y aislamiento

- El hospital exige JWT Keycloak firmado, audiencia/issuer válidos, `sub` GUID y `tenant_id` GUID no vacío. No acepta tenant en body, query ni un header alternativo.
- Lee los roles desde `realm_access.roles`, con etiquetas exactas: `Recepción`, `Admisión`, `Enfermería`, `Médicos`, `Odontólogos`, `Nutricionistas`, `Administrador`.
- Roles del CRM `platform_admin`, `admin`, `supervisor`, `agent`, `doctor` deben mapearse explícitamente en CRM; no se deben inventar equivalencias que el hospital no reconoce. Administrador no tiene acceso clínico en Hospital.
- Cada tenant CRM usa una configuración de hospital independiente. Su identificador debe coincidir con el `tenant_id` del token de servicio. El adaptador rechaza token ajeno, expirado o sin claim antes de hacer llamadas. El hospital sigue validando firma/issuer/audience; decodificar localmente el payload sólo evita errores de enrutamiento de credenciales configuradas.
- Registrar el HttpClient con timeout de 30 segundos, máximo de respuesta razonable y redirecciones deshabilitadas. No aplicar retries automáticos a escrituras.

```csharp
services.AddHttpClient<HospitalClient>(client => {
    client.Timeout = TimeSpan.FromSeconds(30);
    client.MaxResponseContentBufferSize = 20 * 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
```

Configurar secretos por entorno (UUID ilustrativo, sin credenciales reales):

```text
Hospital__Tenants__11111111-1111-1111-1111-111111111111__BaseUrl=https://hospital.example/
Hospital__Tenants__11111111-1111-1111-1111-111111111111__TokenEndpoint=https://identity.example/realms/hospital/protocol/openid-connect/token
Hospital__Tenants__11111111-1111-1111-1111-111111111111__ClientId=recepcion-hospital-1
Hospital__Tenants__11111111-1111-1111-1111-111111111111__ClientSecret=<secret-store>
Hospital__Tenants__11111111-1111-1111-1111-111111111111__AllowClinicalDelivery=false
```

Para pruebas se admite `AccessToken` en lugar de client credentials, pero expirará y deberá renovarse. El token de servicio necesita el mapper `tenant_id` del mismo hospital y únicamente los permisos de su función. Nunca compartir un token de plataforma entre hospitales. HTTPS es obligatorio fuera de una red privada de contenedores confiable; el adaptador admite HTTP para esa red interna.

## Contratos existentes

| Operación | HTTP real | Condiciones |
|---|---|---|
| Paciente vinculado | `GET /v1/patients/{patientId}` | Responde `patientId`, `givenNames`, `familyNames`, `phone`, etc. El adaptador devuelve una proyección mínima. |
| Buscar paciente | `GET /v1/patients/search?queryShape=...&term=...` | Sólo `dui`, `record-number`, `name-tokens`; **no existe búsqueda por teléfono**. |
| Disponibilidad | `GET /v1/agenda/booking-options?from=YYYY-MM-DD&to=YYYY-MM-DD&clinicianId=UUID` | `professionals[].days[].slots[]`: `offered`, `takenBy`, `startsAt`, `durationMinutes`. Respetar `maxDaysPerQuery` y estados de día. |
| Citas de paciente | `GET /v1/agenda/patients/{patientId}?from=YYYY-MM-DD&to=YYYY-MM-DD` | Nueva ruta aplicada en Hospital, máximo 31 días; ver `integrations/hospital/PATIENT-AGENDA.md`. |
| Agenda | `GET /v1/agenda/day?clinicalDay=YYYY-MM-DD` | Vista de personal; no pasar filas de otros pacientes al LLM de una conversación. |
| Comprobar cita | `GET /v1/agenda/{id}?patientId=UUID` | Hospital valida pertenencia exacta. El adaptador repite comparación antes de mutar. |
| Agendar | `POST /v1/agenda` | `{patientId, clinicianId, startsAt, durationMinutes, visitKind}` → `{appointmentId,status,overlaps}`. |
| Reprogramar | `POST /v1/agenda/{id}/reschedule` | `{startsAt,clinicianId,durationMinutes}` → 204. |
| Cancelar por paciente | `POST /v1/agenda/{id}/cancel` | `{reason:"patient-requested",cancelledByPatient:true}` → 204. Nunca DELETE. |
| Listar recetas por bot | `POST /v1/reception/patients/{id}/prescriptions/list` | Body phone/cursor; sólo IDs signed. Requiere cuenta servicio dedicada y teléfono actual. |
| Consultar receta ya emitida | `POST /v1/reception/prescriptions/{id}` | Body phone; DTO mínimo, paciente coincidente, signed. |
| PDF receta por bot | `POST /v1/reception/prescriptions/{id}/pdf` | Body phone → application/pdf. Reutiliza mapper/renderer y perfil del firmante. |

`visitKind`: `first-visit`, `follow-up`, `procedure`, `results`, `paperwork`, `same-day-urgent`. Las fechas de citas llevan offset; los días de agenda pertenecen a la zona horaria del hospital.

## Verificación del paciente

La primera vinculación se hace por personal autorizado: el contacto CRM guarda el `patientId` y el teléfono WhatsApp de la conversación. Antes de cada acción privada se consulta el paciente hospitalario y se exige igualdad del teléfono normalizado completo. No se adivina código de país ni se compara un sufijo. Un teléfono ausente/cambiado o paciente no vinculado causa transferencia a humano. Familiares, tutores y teléfonos compartidos requieren vinculación explícita y autorización distinta; el adaptador no concede ese acceso por coincidencia con un contacto de emergencia.

## Límites reales y decisiones

1. **Carrera de agenda:** el hospital admite solapamientos deliberadamente y devuelve `overlaps`. Consultar slots ofrecidos antes de reservar evita la mayoría de conflictos pero no da exclusión atómica. La UI debe mostrar y derivar un `overlaps=true`; un timeout después del POST es resultado incierto que exige reconciliar, nunca repetir ciegamente. Un endpoint de reserva con idempotencia y bloqueo del slot sería la ampliación mínima para garantizar reservas automáticas sin doble cita.
2. **Recetas por agente:** las rutas clínicas actuales requieren un rol clínico. `AllowClinicalDelivery` permanece desactivado para el bot hasta desplegar el puente estrecho descrito en `integrations/hospital`. Se requieren ambos flags AllowClinicalDelivery y UseReceptionBridge; no existe fallback hacia rutas clínicas generales. Activar los flags no otorga permiso al hospital. Dar al bot el rol `Médicos` para desbloquearlo sería un defecto.
3. **Contenido clínico:** el agente no firma, cancela, repite ni modifica recetas; interpreta únicamente instrucciones textuales ya emitidas. Dudas sobre dosis nuevas, tratamiento o síntomas se transfieren al doctor.
4. **Adjuntos:** el PDF se obtiene como bytes en memoria. No se publica una URL permanente que revele recetas, ni se devuelve la URL de almacenamiento interno al usuario.
5. **Google Calendar:** Hospital no publica integración Google Calendar. El CRM puede sincronizar agenda en una dirección con sondeo periódico y clave de evento por tenant/cita, conservando Hospital como fuente. Cambios externos no deben sobrescribir su agenda sin un contrato de conciliación. El espejo de Google debe llevar título genérico y datos mínimos, nunca receta/diagnóstico.

## Verificación ejecutada

`tests/hospital-client/HospitalClient.Checks.csproj` es un ejecutable sin paquetes externos. Usa fixtures sintéticos y un HttpMessageHandler controlado; prueba teléfono, tenant, expiración, IDOR de citas/recetas, rechazo clínico antes de red, errores sin contenido sensible, ruta/body de creación y propagación de solapamientos.

Compilado y ejecutado con SDK 8 en modo de compatibilidad porque el host sólo ofrece ese SDK:

```text
dotnet msbuild tests/hospital-client/HospitalClient.Checks.csproj /p:TargetFramework=net8.0 /p:RestoreConfigFile=/tmp/recepcion-hospital-checks.NuGet.Config /t:Restore,Build
dotnet tests/hospital-client/bin/Debug/net8.0/HospitalClient.Checks.dll
PASS: hospital client phone linkage, tenant/expiry guard, cross-patient appointment and prescription isolation, clinical gate, error redaction, exact booking contract and overlap signal.
```

Con SDK 10: `dotnet run --project tests/hospital-client/HospitalClient.Checks.csproj`. Esto prueba el adaptador, no sustituye pruebas reales de Keycloak, base de datos ni el bridge en Hospital.

Ese mismo comando se ejecutó posteriormente en `mcr.microsoft.com/dotnet/sdk:10.0` y también pasó. El puente ya tiene patch compilado en copia aislada del hospital y pruebas dirigidas: 33 del servicio/HTTP-JWT, 12 de arquitectura, 3 de baseline OpenAPI y 1 del cliente TypeScript generado, sin fallos ni omitidas. Ver `integrations/hospital/README.md` para evidencia y verificaciones de despliegue pendientes.

## Ampliación de agenda (2026-10-02)

La bandeja consulta próximas citas por paciente vinculado mediante el nuevo endpoint acotado. Configura `UsePatientAgenda=true` al desplegar la nueva API para que también lo use el agente. La UI de Agenda comunica errores de disponibilidad, conserva la clave del intento y registra acciones exitosas en el historial CRM. La validación usa la zona horaria del hospital, incluido un horario UTC que corresponde al día clínico anterior.

Conexión local autorizada y activa desde el 2026-10-03. Crear, consultar, reprogramar y cancelar pasó desde API e interfaz de Recepción, contrastando cada estado con Hospital. Ver `integrations/hospital/PATIENT-AGENDA.md` para evidencia y procedimiento.
