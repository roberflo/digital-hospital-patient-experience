# Plan — reporte de uso por clínica, etapa 1

Plan para `docs/usage-report.md`. Sin migración.

1. **Rojo**: `backend.Tests/UsageReportTests.cs` (AC 2–13) contra `UsageReport.Read`, que aún no existe; añadir `GET /api/platform/usage` a la lista de `PlatformOwnerTests` (AC 1).
2. **Verde**: `backend/UsageReport.cs`.
   - Lee `Tenants` (id, nombre, zona). Agrupa por zona y calcula `[inicio, fin)` del mes en UTC por zona.
   - Por zona, tres lecturas agrupadas con `IgnoreQueryFilters` y `ids.Contains(TenantId)`: mensajes por (`TenantId`, `Sender`, `Type`); conversaciones distintas del agente por `TenantId`; acciones del agente por (`TenantId`, `Kind`). Una lectura de canales para todas.
   - Proyecciones solo de columnas en claro (nunca `Body`).
   - Audita `usage.read` por clínica (un `SaveChanges` por clínica: el guardia de `CrmDb` exige un inquilino por guardado).
   - Mapeo en `PlatformOwner.MapPlatformOwner` con `PlatformOperable(NeedsTenant: false)`.
3. **Web**: `frontend/src/components/platform-usage.tsx` (tarjeta con `<input type="month">`, `Table` de shadcn, totales en `<tfoot>`), montada en `platform-workspace.tsx` bajo los selectores; `platform/usage` en `TENANTLESS` de `crm-proxy.ts`.
4. **Playwright**: `frontend/tests/platform-usage.spec.ts`, entra como `dev-plataforma` y simula `/api/crm/platform/usage*` (tabla, totales, «sin medir», vacío, error con reintento, 360 px sin desplazamiento horizontal de la página).

Riesgos: los mensajes no tienen índice por (`TenantId`, `CreatedAt`); una lectura mensual recorre el índice por conversación. Se acepta y se marca `ponytail:`.

No se hace: parte B (tokens), precios, exportar CSV, historial de canales.
