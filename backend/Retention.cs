using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace Recepcion;

/// <summary>MR-39: finished turns and idempotency receipts are kept 30 days; the agent's pending actions, until they expire (MR-40). Pending, running and uncertain
/// jobs are never deleted (INV-6). A Kapso redelivery past 30 days still stops at the message's ExternalId,
/// which is never swept. Runs inside the tenant's scope: the query filter keeps it to that tenant.</summary>
public static class Retention
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);
    // ponytail: once a day per process, in memory; each replica sweeps once a day (the deletes are idempotent). A shared last-run row if that ever costs.
    static readonly ConcurrentDictionary<Guid, DateOnly> swept = new();
    /// <summary>Deletes in statements of <paramref name="batch"/> rows, at most <paramref name="cap"/> per table a day; what is left waits for tomorrow.
    /// A failure is logged by type only and the day still counts as swept, so a broken sweep is retried tomorrow, not on every tick.</summary>
    public static async Task Daily(CrmDb d, Guid tenant, ILogger log, CancellationToken ct, int batch = 5000, int cap = 100_000)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (swept.TryGetValue(tenant, out var day) && day == today) return;
        swept[tenant] = today;
        var cutoff = DateTimeOffset.UtcNow - Keep;
        try
        {
            for (int n = 0, deleted = batch; deleted == batch && n < cap; n += deleted)
                deleted = await d.Jobs.Where(x => (x.Status == "done" || x.Status == "failed") && x.CreatedAt < cutoff).OrderBy(x => x.CreatedAt).Take(Math.Min(batch, cap - n)).ExecuteDeleteAsync(ct);
            for (int n = 0, deleted = batch; deleted == batch && n < cap; n += deleted)
                deleted = await d.Receipts.Where(x => x.CreatedAt < cutoff).OrderBy(x => x.CreatedAt).Take(Math.Min(batch, cap - n)).ExecuteDeleteAsync(ct);
            // MR-40: the agent's working state past its life is nobody's any more, consumed or not.
            var now = DateTimeOffset.UtcNow;
            for (int n = 0, deleted = batch; deleted == batch && n < cap; n += deleted)
                deleted = await d.PendingActions.Where(x => x.ExpiresAt < now).OrderBy(x => x.ExpiresAt).Take(Math.Min(batch, cap - n)).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { log.LogWarning("Retention sweep failed {ErrorType}. Tenant {TenantId}.", ex.GetBaseException().GetType().Name, tenant); }
    }
}
