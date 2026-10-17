using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace Recepcion;

/// <summary>MR-39: finished turns and idempotency receipts are kept 30 days. Pending, running and uncertain
/// jobs are never deleted (INV-6). A Kapso redelivery past 30 days still stops at the message's ExternalId,
/// which is never swept. Runs inside the tenant's scope: the query filter keeps it to that tenant.</summary>
public static class Retention
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);
    // ponytail: once a day per process, in memory; each replica sweeps once a day (the deletes are idempotent). A shared last-run row if that ever costs.
    static readonly ConcurrentDictionary<Guid, DateOnly> swept = new();
    public static async Task Daily(CrmDb d, Guid tenant, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (swept.TryGetValue(tenant, out var day) && day == today) return;
        var cutoff = DateTimeOffset.UtcNow - Keep;
        await d.Jobs.Where(x => (x.Status == "done" || x.Status == "failed") && x.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
        await d.Receipts.Where(x => x.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
        swept[tenant] = today;
    }
}
