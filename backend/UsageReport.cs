using System.Globalization;
using Microsoft.EntityFrameworkCore;
namespace Recepcion;

/// <summary>Null AI columns: not metered yet (docs/usage-report.md, parte B).</summary>
public record UsageCounts(int NumbersConnected, int NumbersEnabled, int PatientMessages, int AgentReplies, int HumanReplies, int TemplatesSent, int AgentConversations, int Appointments, int Registrations, int Prescriptions,
    long? AiCalls, long? InputTokens, long? OutputTokens, long? CachedInputTokens);
public record ClinicUsage(Guid Id, string Name, UsageCounts Usage);
public record MonthlyUsage(string Month, bool AiMetered, IReadOnlyList<ClinicUsage> Tenants, UsageCounts Totals);

/// <summary>docs/usage-report.md: what each clinic used in a month, for the platform owner. Counts of plain columns only: no body,
/// name or phone is read (INV-U1). The only cross-tenant read of these tables for this purpose (INV-U3).</summary>
public static class UsageReport
{
    static readonly string[] Actions = ["appointment", "patient_registered", "prescription_delivered"];

    public static async Task<MonthlyUsage> Read(string? month, CurrentUser u, CrmDb db, TenantScope scope, CancellationToken ct)
    {
        if (!u.Platform) throw new AccessDeniedException();
        if (month is not { Length: 7 } || !DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
            throw new ArgumentException("Mes inválido: escribe año y mes como 2026-10.");
        var tenants = await db.Tenants.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id).Select(x => new { x.Id, x.Name, x.TimeZone }).ToListAsync(ct);
        var channels = await db.Channels.IgnoreQueryFilters().Where(x => !x.PhoneNumberId.StartsWith("demo"))
            .GroupBy(x => x.TenantId).Select(g => new { g.Key, Connected = g.Count(), Enabled = g.Count(x => x.Enabled) }).ToDictionaryAsync(x => x.Key, ct);
        var messages = new Dictionary<(Guid, string, string), int>(); var conversations = new Dictionary<Guid, int>(); var actions = new Dictionary<(Guid, string), int>();
        // Each clinic's month starts at its own local midnight: one read per distinct zone, never one per clinic.
        // ponytail: Messages has no (TenantId, CreatedAt) index; a monthly read walks the conversation index. Add one if it gets slow.
        foreach (var zone in tenants.GroupBy(x => x.TimeZone))
        {
            var tz = TimeZoneInfo.TryFindSystemTimeZoneById(zone.Key, out var found) ? found : TimeZoneInfo.FindSystemTimeZoneById("America/El_Salvador");
            DateTimeOffset At(DateTime local) => new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime(); // Npgsql writes UTC only
            var (from, to) = (At(first), At(first.AddMonths(1)));
            var ids = zone.Select(x => x.Id).ToArray();
            foreach (var m in await db.Messages.IgnoreQueryFilters().Where(x => ids.Contains(x.TenantId) && x.CreatedAt >= from && x.CreatedAt < to && !x.IsHistory && x.Status != "failed")
                .GroupBy(x => new { x.TenantId, x.Sender, x.Type }).Select(g => new { g.Key.TenantId, g.Key.Sender, g.Key.Type, Count = g.Count() }).ToListAsync(ct))
                messages[(m.TenantId, m.Sender, m.Type)] = m.Count;
            var agent = db.Activities.IgnoreQueryFilters().Where(x => ids.Contains(x.TenantId) && x.CreatedAt >= from && x.CreatedAt < to && x.ActorRole == "agent_ai" && x.ConversationId != null);
            foreach (var c in await agent.Select(x => new { x.TenantId, x.ConversationId }).Distinct().GroupBy(x => x.TenantId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
                conversations[c.Key] = c.Count;
            foreach (var a in await agent.Where(x => Actions.Contains(x.Kind)).GroupBy(x => new { x.TenantId, x.Kind }).Select(g => new { g.Key.TenantId, g.Key.Kind, Count = g.Count() }).ToListAsync(ct))
                actions[(a.TenantId, a.Kind)] = a.Count;
        }
        int Sent(Guid id, string sender) => messages.Where(x => x.Key.Item1 == id && x.Key.Item2 == sender).Sum(x => x.Value);
        var rows = tenants.Select(t => new ClinicUsage(t.Id, t.Name, new UsageCounts(
            channels.GetValueOrDefault(t.Id)?.Connected ?? 0, channels.GetValueOrDefault(t.Id)?.Enabled ?? 0,
            Sent(t.Id, "patient"), Sent(t.Id, "agent"), Sent(t.Id, "human"), messages.GetValueOrDefault((t.Id, "system", "template")),
            conversations.GetValueOrDefault(t.Id), actions.GetValueOrDefault((t.Id, "appointment")), actions.GetValueOrDefault((t.Id, "patient_registered")), actions.GetValueOrDefault((t.Id, "prescription_delivered")),
            null, null, null, null))).ToList();
        var all = rows.Select(x => x.Usage).ToList();
        var totals = new UsageCounts(all.Sum(x => x.NumbersConnected), all.Sum(x => x.NumbersEnabled), all.Sum(x => x.PatientMessages), all.Sum(x => x.AgentReplies), all.Sum(x => x.HumanReplies),
            all.Sum(x => x.TemplatesSent), all.Sum(x => x.AgentConversations), all.Sum(x => x.Appointments), all.Sum(x => x.Registrations), all.Sum(x => x.Prescriptions), null, null, null, null);
        // The owner read every clinic's counts: each one keeps that in its own trail. CrmDb guards one tenant per save.
        // ponytail: one insert per clinic per read; batch with a raw insert if clinics reach the thousands.
        foreach (var t in rows) { scope.Id = t.Id; db.Audits.Add(new Audit { TenantId = t.Id, Actor = u.Subject, Action = "usage.read", Resource = month }); await db.SaveChangesAsync(ct); }
        return new(month, false, rows, totals);
    }
}
