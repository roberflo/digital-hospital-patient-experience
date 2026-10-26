# Reporte de uso por clínica — etapa 1

Petición del propietario el 2026-10-25: «Etapa 1 del reporte de uso por clínica». Estado: **aceptada**.

Un solo proyecto de Kapso atiende a muchas clínicas: cada clínica es un `Tenant` con su propio cliente Kapso y sus números de WhatsApp (`meta_billing_mode` `customer_managed`: la clínica paga a Meta directamente). El dueño de plataforma cobrará a cada clínica una suscripción con una cuota de IA incluida; para eso necesita medir, por clínica y por mes, cuánto usó cada una. Esta etapa **mide**; no cobra, no pone precio y no corta servicio.

La identidad de plataforma y su compuerta son las de `docs/platform-owner.md` (REC-1): rol `platform-owner`, sin `tenant_id`, solo alcanza las rutas marcadas `PlatformOperable`.

## Lenguaje

- **Mes** de una clínica: del día 1 a las 00:00 al día 1 del mes siguiente a las 00:00, **en la zona horaria de esa clínica** (`Tenant.TimeZone`). Un mensaje de las 23:30 del 31 en San Salvador cuenta en ese mes aunque en UTC ya sea el 1.
- **Número conectado**: un `Channel` de la clínica que no es de demostración (`PhoneNumberId` no empieza por `demo`), la misma regla que `whatsAppConnected` en `GET /api/platform/tenants`. **Habilitado**: además `Enabled`.
- **Mensaje del paciente**: `Message` con `Sender = patient`. **Respuesta del agente**: `Sender = agent`. **Respuesta de una persona**: `Sender = human` (incluye lo que el equipo escribe desde la app de WhatsApp en coexistencia). **Plantilla enviada**: `Sender = system` y `Type = template` (los recordatorios por el número de la clínica).
- Un mensaje **histórico** (`IsHistory`, importado al conectar en coexistencia) no es uso del mes y no cuenta. Un envío que **falló** (`Status = failed`: no salió) no cuenta; `sending` e `uncertain` sí (pudieron salir).
- **Conversación atendida por el agente**: conversación con al menos una `Activity` de `ActorRole = agent_ai` en el mes (la regla de `Attended` en `AgentMetrics`).
- **Acción del agente**: `Activity` de `ActorRole = agent_ai` con `Kind` `appointment` (agendar, cambiar o cancelar una cita), `patient_registered` o `prescription_delivered` — los tipos de `AgentMetrics`. Una cita que agenda una persona no es del agente.
- **Uso de IA**: llamadas al modelo y tokens de entrada, salida y entrada en caché, por modelo. **Sin medir** en esta entrega (ver «Parte B»).

## Invariantes

- **INV-U1** El reporte nunca lee el cuerpo de un mensaje, de una actividad ni ningún campo cifrado; solo cuenta filas por columnas en claro.
- **INV-U2** Cada conteo de una fila sale solo de filas con el `TenantId` de esa clínica; nunca se mezclan clínicas.
- **INV-U3** Solo el dueño de plataforma lo lee; la consulta sin filtro de inquilino (`IgnoreQueryFilters`) vive solo en ese manejador.
- **INV-U4** La fila de totales es la suma exacta de las filas.

## Contrato

`GET /api/platform/usage?month=YYYY-MM` — plataforma, **sin** `X-Acting-Tenant` (`PlatformOperable(NeedsTenant: false)`).

```json
200 {
  "month": "2026-10",
  "aiMetered": false,
  "tenants": [ { "id": "uuid", "name": "string", "usage": { …conteos… } } ],
  "totals": { …conteos… }
}
```

Conteos: `numbersConnected`, `numbersEnabled`, `patientMessages`, `agentReplies`, `humanReplies`, `templatesSent`, `agentConversations`, `appointments`, `registrations`, `prescriptions` (enteros) y `aiCalls`, `inputTokens`, `outputTokens`, `cachedInputTokens` (`null` mientras `aiMetered` es `false`).

Una fila por `Tenant` (también una clínica en modo `reminders` o sin actividad: sale en ceros), ordenadas por nombre y luego id. `month` ausente o que no sea `YYYY-MM` → 400.

## Criterios

| AC | Criterio | Cómo se prueba |
|---|---|---|
| 1 | La ruta está marcada `PlatformOperable(NeedsTenant: false)` y entra en la lista exacta de operables de plataforma | `PlatformOwnerTests.MarkedRoutesAreExactlyTheSpecsList` (la lista cambia a propósito: es la política) |
| 2 | Cualquier otro rol (`admin`, `agent`, `doctor`, vacío) → 403; el manejador no consulta nada antes de negarse | `UsageReportTests.OnlyThePlatformOwnerReadsUsage` (lanza `AccessDeniedException`, que `Hosting.Errors` vuelve 403) |
| 3 | Un usuario de hospital nunca alcanza la ruta con la cabecera de actuar (INV-R5): la compuerta responde 403 | `UsageReportTests.AHospitalTokenWithTheActingHeaderIsRefused` sobre `PlatformOwner.Gate` |
| 4 | Una fila por clínica con id y nombre; los conteos de mensajes por las cuatro clases de **Lenguaje**, sin históricos ni fallidos | `UsageReportTests.CountsEachClinicsMonthByClass`, dos clínicas con datos sembrados distintos |
| 5 | Números conectados y habilitados por clínica, sin los de demostración | El mismo caso |
| 6 | Conversaciones atendidas por el agente (distintas) y acciones del agente por tipo; las de una persona no cuentan | El mismo caso |
| 7 | Bordes de mes en la zona de cada clínica: un mensaje a las 23:30 del último día en `America/El_Salvador` cuenta en ese mes; uno a las 00:30 del día 1 local cuenta en el siguiente; una clínica en otra zona usa la suya | `UsageReportTests.MonthBoundariesAreEachClinicsLocalMidnight` |
| 8 | INV-U2: las filas de otra clínica no cambian los conteos de la primera | `CountsEachClinicsMonthByClass` (la segunda clínica tiene datos y se comprueba por separado) |
| 9 | INV-U4: `totals` es la suma de las filas | `UsageReportTests.TotalsAreTheSumOfTheRows` |
| 10 | Sin PHI en el cable: los campos son exactamente los del contrato; un nombre de contacto, teléfono o cuerpo sembrados no aparecen | `CountsEachClinicsMonthByClass` serializa como la API y busca las cadenas |
| 11 | Las columnas de IA son `null` y `aiMetered` es `false` mientras no exista la medición (parte B) | El mismo caso |
| 12 | Cada lectura deja un `Audit` `usage.read` en cada clínica que muestra, con `Actor = platform:<sub>` y `Resource` = el mes | `UsageReportTests.EveryReadIsAuditedInEachClinicItShows` |
| 13 | `month` ausente o mal formado → 400, sin auditar | `UsageReportTests.AMalformedMonthIsABadRequest` |
| 14 | Consultas agrupadas: un número fijo de lecturas por zona horaria distinta, nunca una por clínica | Revisión del diff (`UsageReport.Read`); `ponytail:` con el techo |
| 15 | Web: en la vista del dueño sin recepción elegida, sección «Uso del mes» con selector de mes (por omisión el actual), tabla por clínica y fila de totales; números alineados a la derecha con `tabular-nums`; IA como «sin medir»; estados de carga, vacío y error con reintento («la lectura falló: no significa cero»); usable a 360 px (la tabla se desplaza dentro de su tarjeta, la página no) | `frontend/tests/platform-usage.spec.ts` con la API simulada, escritorio y móvil |
| 16 | El proxy deja pasar la ruta sin recepción elegida (`TENANTLESS`) | La misma prueba de Playwright (sin cookie de elección) |

## Anti-criterios

- Leer o descifrar `Message.Body`, `Activity.Body`, nombres o teléfonos de contactos para contar.
- Contar en una clínica filas de otra, o un total que no sea la suma de las filas.
- `IgnoreQueryFilters` fuera de este manejador para esto, o una ruta que un usuario de hospital pueda leer.
- Estimar dinero: ni precios de Meta ni de IA ni del plan. Solo conteos y tokens.
- Una consulta por clínica (N+1).
- Pedir o aceptar la cabecera `X-Acting-Tenant`: el reporte es de todas las clínicas.
- Un 200 con ceros cuando la lectura falla: la web dice que falló.

## Preguntas abiertas

- **Asumida** — el mes se corta en la zona de cada clínica (`Tenant.TimeZone`, por omisión `America/El_Salvador`); una zona que el host no conoce se lee como `America/El_Salvador`.
- **Asumida** — números conectados y habilitados son el estado **al leer**, no el del mes: no hay historial de canales. Si hace falta «números activos durante el mes», requiere guardar altas y bajas.
- **Asumida** — solo conteos y tokens; los precios y el cálculo del cobro llegan en otra etapa.
- **Asumida** — los recordatorios que salen por el **número general** de la plataforma (`ChannelId = null`) no son mensajes de la clínica y no cuentan aquí; los paga la plataforma. Si se quieren en el reporte, son otra columna.
- **Asumida** — `appointment` cuenta agendar, cambiar y cancelar por igual (así lo escribe el agente).
- **Asumida** — la fecha de un mensaje es su `CreatedAt` (la de WhatsApp para los entrantes, la de creación para los salientes).
- **Asumida** — se audita cada lectura en cada clínica mostrada, como `reminder_doubts.read`; con cientos de clínicas son cientos de filas por lectura.
- **Bloqueante para la parte B** — la medición de tokens toca `backend/AgentGateway.cs`, que otra sesión estaba editando: no se empezó. Necesita una tabla nueva (`AiUsage`) y por tanto una migración ya aprobada por el propietario.

## Parte B — medición de IA (pendiente)

Registrar cada llamada al modelo (`ChatResponse.Usage`: `InputTokenCount`, `OutputTokenCount`, `CachedInputTokenCount`) por clínica, día y modelo en `AiUsage(TenantId, Day, Model, Calls, InputTokens, OutputTokens, CachedInputTokens)` con upsert atómico, desde un `DelegatingChatClient` en la pasarela; el respaldo cuenta contra el modelo que respondió. Al llegar, `aiMetered` pasa a `true` y las cuatro columnas se llenan.
