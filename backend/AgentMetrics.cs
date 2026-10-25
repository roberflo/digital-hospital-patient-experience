using Microsoft.EntityFrameworkCore;

namespace Recepcion;

/// <summary>How the agent's attention went over a period, counted from its trail (docs/reception-agent.md, criterio 66).
/// Kinds and roles only: no body is read.</summary>
public sealed record AgentMetrics(int Days, int Attended, int WithoutPerson, int HandedOver, int Appointments, int Registrations, int Prescriptions, int PersonOffers, int Withheld, int ProviderFailures, int WaitingNotices,
    int RemindersSent, int RemindersConfirmed, int ChangeRequests, int? FirstReplyMinutes)
{
    public static async Task<AgentMetrics> Read(CrmDb db, int? days, CancellationToken ct)
    {
        var span = Math.Clamp(days ?? 7, 1, 90); var since = DateTimeOffset.UtcNow.AddDays(-span);
        var trail = db.Activities.Where(x => x.CreatedAt >= since && x.ConversationId != null);
        var own = trail.Where(x => x.ActorRole == "agent_ai");
        var attended = await own.Select(x => x.ConversationId).Distinct().CountAsync(ct);
        var handedOver = await own.Where(x => x.Kind == "handoff").Select(x => x.ConversationId).Distinct().CountAsync(ct);
        // Withheld answers and provider failures are recorded by the system about the agent's turn.
        var kinds = await trail.Where(x => x.ActorRole == "agent_ai" || x.ActorRole == "system").GroupBy(x => x.Kind).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        int Of(string kind) => kinds.GetValueOrDefault(kind);
        // Criterio 80: what reminders resolved, from their rows (a reminder sent in the period) and the trail.
        var reminders = db.AppointmentReminders.Where(x => x.AttemptedAt >= since && (x.Status == "sent" || x.Status == "confirmed"));
        var sent = await reminders.CountAsync(ct); var confirmed = await reminders.CountAsync(x => x.Status == "confirmed", ct);
        return new(span, attended, attended - handedOver, handedOver, Of("appointment"), Of("patient_registered"), Of("prescription_delivered"), Of("handoff_offer"), Of("guard"), Of("agent_provider"), Of("waiting_notice"),
            sent, confirmed, Of("reminder_change_request"), await FirstReply(db, since, ct));
    }

    /// <summary>Criterio 81: median minutes from each conversation's first patient message in the period to the first agent or person message after it.</summary>
    // ponytail: loads the period's message timestamps (no bodies) into memory; move to a SQL window query if a hospital passes ~100k messages in 90 days.
    static async Task<int?> FirstReply(CrmDb db, DateTimeOffset since, CancellationToken ct)
    {
        var messages = await db.Messages.Where(x => x.CreatedAt >= since && !x.IsHistory && (x.Sender == "patient" || x.Sender == "agent" || x.Sender == "human"))
            .Select(x => new { x.ConversationId, x.Sender, x.CreatedAt }).ToListAsync(ct);
        var minutes = messages.GroupBy(x => x.ConversationId).Select(g =>
        {
            var asked = g.Where(x => x.Sender == "patient").Min(x => (DateTimeOffset?)x.CreatedAt);
            var answered = g.Where(x => x.Sender != "patient" && x.CreatedAt > asked).Min(x => (DateTimeOffset?)x.CreatedAt);
            return answered - asked;
        }).OfType<TimeSpan>().Select(x => x.TotalMinutes).Order().ToList();
        if (minutes.Count == 0) return null;
        var middle = minutes.Count / 2;
        return (int)Math.Round(minutes.Count % 2 == 1 ? minutes[middle] : (minutes[middle - 1] + minutes[middle]) / 2);
    }
}
