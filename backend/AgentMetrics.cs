using Microsoft.EntityFrameworkCore;

namespace Recepcion;

/// <summary>How the agent's attention went over a period, counted from its trail (docs/reception-agent.md, criterio 66).
/// Kinds and roles only: no body is read.</summary>
public sealed record AgentMetrics(int Days, int Attended, int WithoutPerson, int HandedOver, int Appointments, int Registrations, int Prescriptions, int PersonOffers, int Withheld, int ProviderFailures, int WaitingNotices)
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
        return new(span, attended, attended - handedOver, handedOver, Of("appointment"), Of("patient_registered"), Of("prescription_delivered"), Of("handoff_offer"), Of("guard"), Of("agent_provider"), Of("waiting_notice"));
    }
}
