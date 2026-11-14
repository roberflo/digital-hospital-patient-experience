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
    /// <summary>Per channel: where the next window starts, the window being read and Kapso's cursor in it when a pass stopped at its page limit, and when it ran.</summary>
    sealed record Cursor(DateTimeOffset Since, DateTimeOffset Until, string? After, DateTimeOffset Ran);
    // ponytail: cursor in memory per process (as Retention.swept): a restart looks back the whole Cap and Message.ExternalId absorbs the repeats.
    // A column if those full-Cap passes at start-up ever cost.
    static readonly ConcurrentDictionary<Guid, Cursor> cursors = new();

    /// <summary>One pass over the tenant's numbers that are due. A failure is logged by type, per channel, and never thrown nor moves the cursor (INV-9).
    /// <paramref name="at"/> is «now», for the tests.</summary>
    public static async Task Run(CrmDb db, TenantScope scope, KapsoClient kapso, IConfiguration config, ILogger log, CancellationToken ct, DateTimeOffset? at = null, int pages = PagesPerPass)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        List<Channel> channels;
        try { channels = await db.Channels.AsNoTracking().Where(x => !x.PhoneNumberId.StartsWith("demo")).ToListAsync(ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning("Message recovery failed {ErrorType}. Tenant {TenantId}.", ex.GetType().Name, scope.Id); return; }
        foreach (var ch in channels)
        {
            var last = cursors.GetValueOrDefault(ch.Id);
            if (last is not null && now < last.Ran + Every) continue;
            var floor = now - Cap; var since = last is null || last.Since < floor ? floor : last.Since;
            var (until, after) = last?.After is { } resume ? (last.Until, resume) : (now - Grace, (string?)null);
            // Written before asking: a failure keeps this since and drops any half-read window, so the next pass asks again from here.
            cursors[ch.Id] = new(since, until, null, now);
            int items = 0, skipped = 0, read = 0; string? next = null; var handedOff = new HashSet<Guid>();
            try
            {
                await foreach (var page in kapso.Inbound(ch.PhoneNumberId, since, until, after, ct))
                {
                    foreach (var item in page.Items)
                    {
                        items++;
                        var k = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("kapso", out var ka) ? ka : default;
                        // Read defensively until G0: an item without id or timestamp is skipped and counted, never the end of the pass.
                        if (WhatsAppEndpoints.Get(item, "id") is null || !long.TryParse(WhatsAppEndpoints.Get(item, "timestamp"), out var unix)) { skipped++; continue; }
                        var sent = DateTimeOffset.FromUnixTimeSeconds(unix);
                        // Only what the patient wrote, inside the window: echoes and history are S3, a younger one is still the webhook's (INV-5, INV-6).
                        if (WhatsAppEndpoints.Get(k, "direction") == "outbound" || WhatsAppEndpoints.Get(k, "origin") == "history_sync" || sent < floor || sent > until) { skipped++; continue; }
                        // The webhook's channel lock: a delivery of the same message meanwhile waits, and then finds it (INV-1).
                        await using var tx = await db.Database.BeginTransactionAsync(ct);
                        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(ch.Id.ToByteArray(), 0)})", ct);
                        // The listed message carries the conversation's fields (phone_number, contact_name) in kapso(), where the webhook has them apart.
                        if (await WhatsAppEndpoints.Ingest(item, item, k, ch, "whatsapp.message.received", db, scope, config, handedOff) is not null) skipped++;
                        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                    }
                    next = page.After;
                    if (++read == pages) break;
                }
                cursors[ch.Id] = next is null ? new(until - Grace, until, null, now) : new(since, until, next, now);
                // Counts and where, never phone, body or message id (INV-8 of reception-agent).
                log.LogInformation("Message recovery: {Items} items, {Skipped} skipped. Tenant {TenantId}, channel {ChannelId}.", items, skipped, scope.Id, ch.Id);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                db.ChangeTracker.Clear(); // the failed item's rows were rolled back; none may ride on the next SaveChanges
                log.LogWarning("Message recovery failed {ErrorType}. Tenant {TenantId}, channel {ChannelId}.", ex.GetType().Name, scope.Id, ch.Id);
            }
        }
    }
}
