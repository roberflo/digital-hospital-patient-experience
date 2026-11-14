using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

/// <summary>A01 S1 (docs/a01-recuperar-mensajes.md): asks Kapso, per number, for the inbound messages the webhook did not deliver and stores them
/// through the webhook's own Ingest, for a person (never the agent). Runs inside the tenant's scope: the query filter keeps it to that tenant (INV-7).</summary>
public static class MessageRecovery
{
    // OQ-A01-1, asumida. Grace: Kapso's ~50 s of retries, plus its ordering and batching, before a message counts as lost (INV-5).
    // Cap: WhatsApp's 24 h reply window, and the farthest any pass looks (INV-6). Every: one pass per number every 5 min; G0.4 may space it.
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(3), Cap = TimeSpan.FromHours(24), Every = TimeSpan.FromMinutes(5);
    /// <summary>Pages of 100 one pass reads; a longer window resumes from Kapso's cursor on the next pass.</summary>
    public const int PagesPerPass = 10;
    public const string Note = "Mensaje del paciente recuperado tras una interrupción de la conexión con WhatsApp. El agente no respondió: revisa y contesta.";
    /// <summary>Whole-pass time budget: a hung Kapso holds the tenant's worker slot, and so its next agent turn, this long at most (M-4).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    /// <summary>Per channel: where the next window starts, and, when a pass stopped at its page limit, the window being read and Kapso's cursor in it.</summary>
    sealed record Cursor(DateTimeOffset Since, DateTimeOffset Until, string? After);
    // ponytail: cursor in memory per process (as Retention.swept): a restart looks back the whole Cap and Message.ExternalId absorbs the repeats.
    // A column if those full-Cap passes at start-up ever cost.
    static readonly ConcurrentDictionary<Guid, Cursor> cursors = new();
    /// <summary>When each tenant's last pass began: checked before anything is read, so a tick between passes costs nothing.</summary>
    static readonly ConcurrentDictionary<Guid, DateTimeOffset> ran = new();
    /// <summary>Per tenant, the channel a pass starts from: the one after the channel a budget cut stopped at, so no number starves behind a stuck one (M-9).</summary>
    static readonly ConcurrentDictionary<Guid, int> start = new();

    /// <summary>One pass over the tenant's numbers, when due. A failure is logged by type, per channel, and never thrown nor moves the cursor (INV-9).
    /// <paramref name="at"/> is «now», for the tests.</summary>
    public static async Task Run(CrmDb db, TenantScope scope, KapsoClient kapso, IConfiguration config, ILogger log, CancellationToken ct, DateTimeOffset? at = null, int pages = PagesPerPass, TimeSpan? budget = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        if (ran.TryGetValue(scope.Id, out var last) && now < last + Every) return;
        ran[scope.Id] = now;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(budget ?? Budget);
        List<Channel> channels;
        try { channels = await db.Channels.AsNoTracking().Where(x => !x.PhoneNumberId.StartsWith("demo")).OrderBy(x => x.Id).ToListAsync(limit.Token); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning("Message recovery failed {ErrorType}. Tenant {TenantId}.", ex.GetType().Name, scope.Id); return; }
        var floor = now - Cap;
        var first = channels.Count == 0 ? 0 : start.GetValueOrDefault(scope.Id) % channels.Count;
        for (var i = 0; i < channels.Count; i++)
        {
            if (limit.IsCancellationRequested) break; // the rest wait, cursors untouched, for the next pass
            var ch = channels[(first + i) % channels.Count];
            var cursor = cursors.GetValueOrDefault(ch.Id);
            // A resumed window keeps its own since with Kapso's cursor (M-1, G0.3); a new one starts no earlier than the Cap.
            var (since, until, after) = cursor?.After is { } resume ? (cursor.Since, cursor.Until, resume) : (cursor is null || cursor.Since < floor ? floor : cursor.Since, now - Grace, (string?)null);
            // done: the cursor after the last page whose items are all saved, where a cut pass resumes (M-9).
            int items = 0, skipped = 0, read = 0; string? next = null, done = after, asking = after; var handedOff = new HashSet<Guid>();
            try
            {
                await foreach (var page in kapso.Inbound(ch.PhoneNumberId, since, until, after, limit.Token))
                {
                    foreach (var item in page.Items)
                    {
                        items++;
                        var k = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("kapso", out var ka) ? ka : default;
                        // Read defensively until G0: an item without id or a usable timestamp is skipped and counted, never the end of the pass (M-2).
                        if (WhatsAppEndpoints.Get(item, "id") is null || WhatsAppEndpoints.SentAt(item) is not { } sent) { skipped++; continue; }
                        // Only what the patient wrote, inside the window: echoes and history are S3, a younger one is still the webhook's (INV-5),
                        // and nothing older than the Cap is saved, whatever the request asked (INV-6).
                        if (WhatsAppEndpoints.Get(k, "direction") == "outbound" || WhatsAppEndpoints.Get(k, "origin") == "history_sync" || sent < floor || sent > until) { skipped++; continue; }
                        // The webhook's channel lock: a delivery of the same message meanwhile waits, and then finds it (INV-1).
                        await using var tx = await db.Database.BeginTransactionAsync(limit.Token);
                        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(ch.Id.ToByteArray(), 0)})", limit.Token);
                        // The listed message carries the conversation's fields (phone_number, contact_name) in kapso(), where the webhook has them apart.
                        if (await WhatsAppEndpoints.Ingest(item, item, k, ch, "whatsapp.message.received", db, scope, config, handedOff) is not null) skipped++;
                        await db.SaveChangesAsync(limit.Token); await tx.CommitAsync(limit.Token);
                    }
                    next = done = page.After;
                    // A cursor Kapso gives back unchanged would never end the window (M-10, G0.3): the window closes here.
                    if (next is not null && next == asking) { next = null; log.LogWarning("Message recovery: Kapso repeated its page cursor; window closed. Tenant {TenantId}, channel {ChannelId}.", scope.Id, ch.Id); break; }
                    asking = next;
                    if (++read == pages) break;
                }
                cursors[ch.Id] = next is null ? new(until - Grace, until, null) : new(since, until, next);
                // Counts and where, never phone, body or message id (INV-8 of reception-agent).
                log.LogInformation("Message recovery: {Items} items, {Skipped} skipped. Tenant {TenantId}, channel {ChannelId}.", items, skipped, scope.Id, ch.Id);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Cut by the budget: like the page limit, the next pass resumes after the last complete page, and starts from the next number (M-9).
                // Any other failure keeps the cursor (INV-9), except a half-read window, dropped so a broken Kapso cursor cannot repeat.
                if (limit.IsCancellationRequested) { if (done is not null) cursors[ch.Id] = new(since, until, done); start[scope.Id] = (first + i + 1) % channels.Count; }
                else if (after is not null) cursors[ch.Id] = new(since, until, null);
                db.ChangeTracker.Clear(); // the failed item's rows were rolled back; none may ride on the next SaveChanges
                log.LogWarning("Message recovery failed {ErrorType}. Tenant {TenantId}, channel {ChannelId}.", ex.GetType().Name, scope.Id, ch.Id);
            }
        }
    }
}
