# Plan: trabajo interrumpido con rastro y salud de las colas

Spec: [agent-jobs-health.md](agent-jobs-health.md). Medido el 2026-10-04; solo lectura, no se
ejecutó ninguna prueba.

**Estado: listo para los criterios 2–10; el criterio 1 tiene una rama bloqueada por una pregunta
clínica (§0).**

## 0. Escalado: una rama sin resolver

INV-2 habla de «toda conversación que el barrido **pasa** a `human`». El criterio 1 y OQ-5 dicen
que `Summary` toma el texto fijo «y sustituye a lo que hubiera». Chocan cuando la conversación
**ya estaba** en `human` al llegar el barrido.

Caso concreto: `Handoff(urgent: true)` (`AgentRuntime.cs:604`) escribe el motivo de urgencia en
`Summary`. Si el turno muere después (por ejemplo, el envío de `:611` se cuelga), cinco minutos
más tarde el barrido reemplaza ese motivo por «Atención automática interrumpida…». El motivo sigue
en la `Activity` del agente, pero desaparece del resumen.

- **Opción A (literal de OQ-5):** siempre sobrescribir.
- **Opción B:** si ya estaba en `human`, se añade la `Activity` pero `Summary` se conserva.

Es qué ve primero quien atiende una posible urgencia: lo contesta el dueño del producto. El resto
del plan no depende de la respuesta: es una condición en una línea del paso 3 y un assert en T1.

## 1. Estado del árbol

- `AgentRuntime.cs` está limpio: lo que estaba sin confirmar entró como `139f4e2`.
- Líneas vigentes en el árbol de trabajo: `AgentRuntime.cs:616-644` (worker; barrido `:630-632`,
  selección `:633`, reclamo `:634`, plazo `:636`); `AppointmentReminders.cs:44` (10 min) y
  `:85-91` (candado, relectura, 15 min); `CrmEndpoints.cs:93` (`/jobs`); `Models.cs:247`.
- `main` local va 9 adelante y 4 atrás de `origin/main`; tres de los cuatro commits de origin
  tienen el mismo título que tres locales con otro hash (historia reescrita).
- La clase `AgentWorker` es idéntica byte a byte en local y en `origin/main` (allí empieza en
  `:419`). El resto del archivo difiere en 286 líneas y chocará al reconciliar, con o sin este
  trabajo.
- Trabajo ajeno sin confirmar que no se toca: `backend/Program.cs`, `HospitalEndpoints.cs`,
  `Integrations/HospitalClient.cs`, trece archivos de frontend, y sin seguimiento
  `InstallationEndpoints.cs`, `InstallationTests.cs`, `AppointmentCancellationTests.cs`. Compila
  dentro de nuestra corrida: si otra sesión rompe el build, el rojo será por la razón equivocada.
- Recepción no tiene `AGENTS.md` ni `CLAUDE.md` de raíz.

**Cómo se corren las pruebas**

- Con cwd en la raíz de Recepción: `scripts/test-backend.sh "<filtro>"`. Usa `$PWD`,
  `--env-file .env` y `docker compose exec db`: no funciona desde otro directorio ni desde un
  worktree privado.
- Necesita el servicio `db` arriba, la red `recepcion_default`, el volumen `recepcion-nuget` y
  `.env` con `POSTGRES_PASSWORD`. Crea `recepcion_tests_<ts>_<pid>`, migra y la borra al salir.
- Un filtro propio **reemplaza** el que excluye `Eval` y `Live`. Usar
  `FullyQualifiedName~AgentIntegrationTests.Abandoned|FullyQualifiedName~AgentIntegrationTests.Claim|FullyQualifiedName~AgentIntegrationTests.JobsHealth`.
  La suite completa de cierre va sin argumento.
- Compila en `bin/` y `obj/` del checkout compartido: dos sesiones a la vez se corrompen.
- `DisableTestParallelization = true` (`AgentGuardTests.cs:12`): las pruebas son secuenciales.

## 2. Archivos que cambian

| Archivo | Cambio |
|---|---|
| `backend/AgentRuntime.cs` | Solo la clase `AgentWorker` (`:616-651`) |
| `backend/CrmEndpoints.cs` | Una ruta tras `:93` y un método estático (solo entrega B) |
| `backend.Tests/AgentIntegrationTests.cs` | Una palabra en `:10`: `sealed class` → `sealed partial class` |
| `backend.Tests/AgentJobsHealthTests.cs` | Nuevo: la otra mitad de la clase parcial |

La clase parcial reutiliza el fixture y los ayudantes privados (`Service`, `Runtime`, `Fake`,
`Json`, `options`, `protection`) sin copiarlos. No se toca `AppointmentReminders.cs`, `Models.cs`,
`Program.cs`, `Migrations/**` ni el frontend.

## 3. Forma del código

En `AgentWorker`:

```csharp
public const int AbandonedAfterMinutes = 5;
public const string AbandonedNote = "Atención automática interrumpida. Revisa el historial antes de responder.";
public static Task RetireAbandoned(CrmDb d, TenantScope scope, ConversationService service, CancellationToken ct)
public static Task<int> Claim(CrmDb d, Guid jobId, CancellationToken ct)
public static Task RunTenant(IServiceProvider sp, Guid tid, ILogger log, CancellationToken stoppingToken)
```

- **`RetireAbandoned`** copia el patrón de `InboxWorkflow.WakeDue`: lee los pares
  `(Id, ConversationId)` de los `Job` vencidos; por cada uno toma `service.Lock(ConversationId)`,
  carga el `Job` y hace `ReloadAsync`; si ya no está en `running` o ya no está vencido, `continue`;
  si sigue vencido: `Job` a `uncertain`, conversación recargada a `human`,
  `Summary = AbandonedNote` (sujeto a §0), `Revision++`, e
  `InboxWorkflow.Event(scope, conv, "Sistema", "handoff", AbandonedNote)`. Un solo
  `SaveChangesAsync` por `Job` (INV-2).
- **`Claim`** es la sentencia de `:634` movida sin cambios.
- **`RunTenant`** es el cuerpo de `:628-644` movido, con `continue` cambiado por `return`.
  `ExecuteAsync` queda en: listar inquilinos, crear scope, llamar a `RunTenant`.

En `CrmEndpoints`:

```csharp
api.MapGet("/jobs/health", (CrmDb db, TenantScope t, CurrentUser u, IConfiguration c) => JobsHealth(db, t, u, c, DateTimeOffset.UtcNow));
public static async Task<object> JobsHealth(CrmDb db, TenantScope t, CurrentUser u, IConfiguration c, DateTimeOffset now)
```

- Primera línea `u.RequireAdmin()` (precedente: `InstallationEndpoints.Read`).
- Respuesta: `{ agent: { pending, oldestPendingSeconds, runningPastDeadline, uncertain24h, failed24h }, reminders: { overduePending, sendingPastDeadline, uncertain24h, lastSyncAt, paused } }`.
- Comentario `// ponytail: conteos sin índice por Status; índice cuando el volumen lo pida (migración)`.

**Asunciones del plan (la spec no las fija):** se anida en `agent` y `reminders` porque
`uncertain24h` aparece dos veces; `oldestPendingSeconds` es `0` sin pendientes; la ventana de 24 h
se mide sobre `Job.StartedAt` y `AppointmentReminder.AttemptedAt` (no hay columna de fin ni
migración); los umbrales de 10 y 15 min quedan como literales duplicados para no tocar
`AppointmentReminders.cs`.

## 4. Orden del trabajo

**Entrega A (criterios 1–6)**

1. **Extracción pura.** `RetireAbandoned` (comportamiento de hoy, sin `Activity`), `Claim` y
   `RunTenant`; `AgentIntegrationTests` pasa a `partial`. Suite completa igual que antes.
2. **Pruebas T1–T5** en `AgentJobsHealthTests.cs`; correr y pegar los mensajes.
3. **Rastro.** `RetireAbandoned` añade `Activity` y `Summary`, todavía sin candado. T1 verde; T3
   rojo por su propia razón.
4. **Candado y relectura.** T3 verde. Suite completa. Corte mínimo entregable.

**Entrega B (criterios 7–10)** — retenida hasta que exista un lector (§7).

5. **Stub.** `JobsHealth` devuelve la forma completa en ceros y sin `RequireAdmin`; se añade la ruta.
6. **Pruebas T6–T9**; rojos pegados.
7. **Consultas reales y `RequireAdmin`.** Verde y suite completa.

## 5. Pruebas por criterio

T1 usa `RunTenant` con un proveedor de una línea:
`new ServiceCollection().AddSingleton(scope).AddSingleton(db).AddSingleton(Service(k)).AddSingleton(Runtime(ai,k)).BuildServiceProvider()`.
El `job` del fixture se pone en `running` con `StartedAt` hace 6 min, para que no quede ningún `pending`.

| Prueba | Criterio | Qué comprueba | Rojo esperado |
|---|---|---|---|
| T1 `AbandonedTurnLeavesATrailOnceAndNeverRuns` | 1, 2, 6 | `RunTenant` dos veces. Una `Activity` (`handoff`, `Sistema`, `system`, cuerpo igual a `AbandonedNote`), `Summary`, `Job` en `uncertain`, conversación en `human`, `Revision` igual tras la segunda pasada, `ai.Calls == 0` y `k.Calls == 0`. | Tras el paso 1: `Assert.Single() Failure: The collection was empty`. |
| T2 `SweepLeavesPendingAndRecentRunningAlone` | 3 | `RetireAbandoned` directo. `pending` y `running` hace 1 min conservan estado; sus conversaciones conservan `Status` y `Revision`; cero `Activity`. | Nace verde. |
| T3 `ConcurrentSweepsRetireOnce` | 4 | Segundo `CrmDb` y `ConversationService` sobre `options`. La prueba toma el candado de la conversación desde un tercer contexto, lanza los dos barridos, espera unos 300 ms y lo suelta. Una `Activity` y `Revision` +1. | Contra el paso 3: `Assert.Single() Failure: The collection contained 2 items`. |
| T4 `ClaimIsWonByOneWorker` | 5 | `AgentWorker.Claim(db, job.Id)` y `Claim(other, job.Id)` con `Task.WhenAll`; la suma es 1. | Nace verde (regresión). |
| T5 (dentro de T1) | 6 | Tras la segunda pasada, `Job.Status` sigue en `uncertain` y `StartedAt` no cambió. | Nace verde. |
| T6 `JobsHealthCountsAgentQueue` | 7 | Filas en cada estado y a cada lado de 5 min y 24 h; los cinco números. | Contra el stub: `Assert.Equal() Failure: Expected: 2, Actual: 0`. |
| T7 `JobsHealthCountsReminders` | 8 | Filas a cada lado de 15 min, 10 min y 24 h; `lastSyncAt`; `paused` en sus dos causas. | Contra el stub: `Assert.Equal()` sobre `overduePending`. |
| T8 `JobsHealthIsTenantScopedAndAdminOnly` | 9 | Segundo inquilino con su propio `TenantScope` y `CrmDb`; los conteos lo ignoran. `Assert.ThrowsAsync<AccessDeniedException>` con rol `agent` y `doctor`. | `Assert.Throws() Failure: No exception was thrown`. |
| T9 `JobsHealthCarriesNoPersonData` | 10 | Contacto `PRIVATE PATIENT SENTINEL` con teléfono centinela. `DoesNotContain` sobre el JSON crudo del nombre, teléfono, `contact.Id`, `PatientId` y `AppointmentId`. Todas las hojas son número o booleano y hay exactamente una cadena que parsea como fecha. | Nace verde contra el stub. |

**Rojos que no son rojos**

- **T3 es probabilístico contra el código del paso 3.** Solo falla si los dos barridos se solapan.
  El código de hoy no fallaría por concurrencia: los dos contextos escriben `Revision = 1`
  (actualización perdida), así que «hoy fallaría» en la spec solo es cierto por la `Activity`
  ausente. Si sale verde contra el paso 3, se anota y no se declara TDD para el criterio 4. Lo
  determinista: ya con candado, quitar la relectura lo pone en rojo.
- **T2, T4, T5 y T9 nacen verdes.** Una mutación por prueba, pegada y revertida: T2 cambiar `-5`
  por `+5`; T4 quitar `&& x.Status == "pending"`; T5 seleccionar también `uncertain`; T9 añadir
  `contact.Name` a la respuesta.
- **En T1 los contadores en 0 son vacíos por sí solos**: con la conversación ya en `human`, `Run`
  vuelve sin llamar al modelo. El assert que carga el criterio 6 es el de T5.

**Huecos declarados**

- **Criterio 1, segundo assert.** Queda probado que `RunTenant` llama al barrido; el enlace
  `ExecuteAsync` → `RunTenant` (un bucle de tres líneas) no tiene prueba. Cerrarlo exige arrancar
  el worker real, que recorre todos los inquilinos de la base de pruebas: lento y contamina
  contadores.
- **Criterio 9, el 403 literal.** Se prueba `AccessDeniedException`; la traducción a 403 es
  `Program.cs:49`, ya existente. No hay `WebApplicationFactory` en `backend.Tests`.

## 6. Riesgos

- **El paso 4 es el más peligroso.** `Lock` es un candado asesor de sesión sobre la conexión del
  `CrmDb`. Si `service` y `d` no salen del mismo scope, el candado no protege nada. En el worker
  salen del mismo scope; en T3 hay que construir cada pareja a mano.
- **El paso 1 mueve el único bucle que atiende pacientes sin prueba previa.** Un `continue` mal
  traducido a `return` cambia el comportamiento. Después se corre la suite completa, no el filtro.
- Dos `Job` vencidos de la misma conversación producen dos `Activity`. Improbable hoy (un worker,
  secuencial); se deja así.
- Conversación resuelta: el barrido ya la deja hoy en `Status = "human"` con `State = "resolved"`.
  Se conserva; se señala, no se arregla.
- Checkout compartido: nunca `git add`. La base del commit la decide quien reconcilie la divergencia.

## 7. Partir la entrega (OQ-4)

Sí. **A (criterios 1–6)** arregla el defecto demostrable. **B (criterios 7–10)** no tiene lector
hoy: el proxy web permite el prefijo `jobs` (`frontend/src/lib/crm-proxy.ts`), pero ningún
componente lee `/jobs`; `/overview` ya publica `pending` y `failed` (`CrmEndpoints.cs:12`) y la
interfaz solo usa `stats.human`. B arrastra además las asunciones sobre qué columna mide las 24 h.
Se retiene hasta que alguien nombre al lector o llegue la pantalla.

## 8. Lo que NO se hace

- Interfaz de cola, despachador común, `SKIP LOCKED`, lease o intentos (esperan a B1 y B2).
- Migración, columna o índice.
- Mensaje al paciente desde el barrido (OQ-1 abierta).
- Asignar al doctor del canal en el abandono.
- Constantes compartidas para 10 y 15 min (obligaría a tocar `AppointmentReminders.cs`).
- Tipo `record` para la respuesta: objeto anónimo hasta que la pantalla necesite el contrato.
- `WebApplicationFactory` o arrancar el worker real.
- Tocar `UncertainDeliveryIsNeverRetried` o `InterruptedReminderShowsUncertainDeliveryWithoutRetry`.
- Límite al barrido, exposición en `/health/ready`, pantalla.
