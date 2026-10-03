using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
namespace Recepcion;

public sealed record ActivityFeedItem(Guid Id, string Kind, string Category, string Actor, string ActorRole,
    string CareType, string Body, DateTimeOffset CreatedAt, Guid? ContactId, string? PatientName,
    Guid? ConversationId, string? ConversationState, string? ChannelName, string? DeliveryStatus);
public sealed record ActivityFeedPage(IReadOnlyList<ActivityFeedItem> Items, int Total, int Page, int PageSize,
    IReadOnlyDictionary<string,int> CareCounts);

public static class ActivityFeed
{
    public static string CareType(string role) => role switch
    {
        "agent_ai" => "ai", "doctor" => "doctor", "agent" => "reception",
        "admin" or "platform_admin" or "supervisor" => "team", "system" => "system", "external" => "external", _ => "unknown"
    };
    public static string Category(string kind) => kind switch
    {
        "response" => "response", "handoff" or "assignment" => "handoff", "note" => "note",
        "appointment" => "appointment", "clinical_review" => "clinical", "error" or "delivery" => "review",
        "agent_tool" => "ai_action", _ when kind.StartsWith("proposal", StringComparison.Ordinal) => "appointment", _ => "crm"
    };
    // Never disclose proposal JSON (patient/doctor identifiers and confirmation codes) in a general feed.
    public static string DisplayBody(Activity activity)
    {
        if (activity.Kind.StartsWith("proposal", StringComparison.Ordinal)) return activity.Kind.StartsWith("proposal_used:", StringComparison.Ordinal) ? "Confirmación de agenda procesada en la conversación." : "Propuesta de agenda enviada para confirmación del paciente.";
        if (activity.Kind == "agent_tool")
        {
            var actions = new Dictionary<string,string> { ["handoff"]="Transferencia a una persona", ["record_note"]="Nota de seguimiento", ["hospital_availability"]="Consulta de disponibilidad", ["my_appointments"]="Consulta de citas del paciente", ["my_prescriptions"]="Consulta de recetas emitidas", ["get_prescription"]="Consulta de una receta", ["send_prescription"]="Envío de receta", ["propose_action"]="Propuesta de cambio en la agenda" };
            foreach(var (name,label) in actions) if(activity.Body.StartsWith($"Herramienta: {name}.", StringComparison.Ordinal)) return label + (activity.Body.Contains("requiere revisión",StringComparison.Ordinal)?": requiere revisión.":": completado.");
        }
        return activity.Body;
    }
    public static string SearchText(string value) => string.Concat(value.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant();

    public static void MapActivityFeed(this WebApplication app) => app.MapGet("/api/activity-feed", Read).RequireAuthorization();
    public static async Task<IResult> Read(CrmDb db, TenantScope scope, string? q, string? care, string? category,
        Guid? contactId, Guid? conversationId, DateOnly? from, DateOnly? to, int? page, CancellationToken ct)
    {
        var careTypes = new[] { "ai", "doctor", "reception", "team", "system", "external", "unknown" };
        var categories = new[] { "response", "handoff", "note", "appointment", "clinical", "review", "ai_action", "crm" };
        if (q?.Length > 150 || page is < 1 or > 10000 || (!string.IsNullOrEmpty(care) && !careTypes.Contains(care))
            || (!string.IsNullOrEmpty(category) && !categories.Contains(category)) || (from is {} start && to is {} end && start > end))
            throw new ArgumentException("Revisa los filtros del historial.");
        var query = db.Activities.AsNoTracking().Where(a => (contactId == null || a.ContactId == contactId) && (conversationId == null || a.ConversationId == conversationId));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(await db.Tenants.Where(t => t.Id == scope.Id).Select(t => t.TimeZone).SingleAsync(ct));
        if (from is {} first) { var instant = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(first.ToDateTime(TimeOnly.MinValue), zone)); query = query.Where(a => a.CreatedAt >= instant); }
        if (to is {} last) { if(last == DateOnly.MaxValue)throw new ArgumentException("Fecha fuera de rango"); var instant = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(last.AddDays(1).ToDateTime(TimeOnly.MinValue), zone)); query = query.Where(a => a.CreatedAt < instant); }
        // Names and event bodies are encrypted at rest. Stream all eligible history; do not search
        // only a truncated recent page. Contact lookup remains tenant-scoped and request-local.
        var contacts = await db.Contacts.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var needle = SearchText(q?.Trim() ?? ""); var items = new List<Activity>();
        var number = page ?? 1; const int size = 30; var total = 0;
        var counts = careTypes.ToDictionary(c => c, _ => 0);
        await foreach (var item in query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(category) && Category(item.Kind) != category) continue;
            var contact = item.ContactId is {} cid ? contacts.GetValueOrDefault(cid) : null;
            if (needle.Length > 0 && !SearchText(string.Join(' ', contact?.Name, contact?.Phone, item.Actor, DisplayBody(item))).Contains(needle)) continue;
            var type = CareType(item.ActorRole); counts[type]++;
            if (!string.IsNullOrEmpty(care) && type != care) continue;
            if (total >= (number - 1) * size && items.Count < size) items.Add(item);
            total++;
        }
        var ids = items.Select(a => a.ConversationId).OfType<Guid>().Distinct().ToArray();
        var conversations = await db.Conversations.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var channelIds = conversations.Values.Select(c => c.ChannelId).Distinct().ToArray();
        var channels = await db.Channels.AsNoTracking().Where(c => channelIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var messageIds = items.Select(a => a.MessageId).OfType<Guid>().ToArray();
        var deliveries = await db.Messages.AsNoTracking().Where(m => messageIds.Contains(m.Id)).Select(m => new { m.Id, m.Status }).ToDictionaryAsync(m => m.Id, m => m.Status, ct);
        var result = items.Select(a => {
            var contact = a.ContactId is {} cid ? contacts.GetValueOrDefault(cid) : null;
            var conversation = a.ConversationId is {} id ? conversations.GetValueOrDefault(id) : null;
            return new ActivityFeedItem(a.Id, a.Kind.StartsWith("proposal", StringComparison.Ordinal) ? "proposal" : a.Kind, Category(a.Kind), a.Actor, a.ActorRole, CareType(a.ActorRole), DisplayBody(a), a.CreatedAt,
                contact?.Id, contact?.Name, conversation?.Id, conversation?.State,
                conversation is null ? null : channels.GetValueOrDefault(conversation.ChannelId)?.Name,
                a.MessageId is {} mid ? deliveries.GetValueOrDefault(mid) : null);
        }).ToArray();
        return Results.Ok(new ActivityFeedPage(result, total, number, size, counts));
    }
}
