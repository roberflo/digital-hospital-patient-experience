using Microsoft.EntityFrameworkCore;
namespace Recepcion;

public record AssignmentItem(Guid Id, long ExpectedRevision);
public record AssignmentInput(AssignmentItem[] Conversations, string? AssignedTo);

public static class TeamEndpoints
{
    public static void MapTeam(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/members/workload", async (CrmDb db) => new
        {
            members = await db.Members.OrderBy(x => x.Name).Select(m => new
            {
                m.Subject, m.Name, m.Role, m.Disabled,
                open = db.Conversations.Count(c => c.AssignedTo == m.Subject && c.State == "open"),
                pending = db.Conversations.Count(c => c.AssignedTo == m.Subject && c.State == "pending"),
                snoozed = db.Conversations.Count(c => c.AssignedTo == m.Subject && c.State == "snoozed")
            }).ToListAsync(),
            unassigned = await db.Conversations.CountAsync(c => c.AssignedTo == null && c.State != "resolved")
        });
        api.MapPost("/conversations/assign", Assign);
    }

    // Attendants can hand off their own or unassigned work. Supervisors can redistribute any work.
    public static void RequireEditable(Conversation conversation, CurrentUser user)
    {
        if (!user.Admin && conversation.AssignedTo is {} owner && owner != user.Subject)
            throw new AccessDeniedException();
    }

    public static async Task<IResult> Assign(AssignmentInput input, CrmDb db, CurrentUser user, TenantScope scope, ConversationService locks)
    {
        if (input.Conversations is null || input.Conversations.Length is < 1 or > 100 || input.Conversations.Any(x => x is null) ||
            input.Conversations.Select(x => x.Id).Distinct().Count() != input.Conversations.Length)
            throw new ArgumentException("Selecciona de 1 a 100 conversaciones distintas");
        if (input.Conversations.Length > 1) user.RequireAdmin();
        using var teamLease = await locks.Lock(scope.Id);
        var target = input.AssignedTo is null ? null : await db.Members.SingleOrDefaultAsync(m => m.Subject == input.AssignedTo && !m.Disabled);
        if (input.AssignedTo is not null && target is null) throw new ArgumentException("La persona no está disponible en este hospital");
        var leases = new List<IDisposable>();
        try
        {
            foreach (var item in input.Conversations.OrderBy(x => x.Id)) leases.Add(await locks.Lock(item.Id));
            var ids = input.Conversations.Select(x => x.Id).ToArray();
            var conversations = await db.Conversations.Where(c => ids.Contains(c.Id)).ToListAsync();
            if (conversations.Count != ids.Length) return Results.NotFound();
            foreach (var row in conversations)
            {
                // A tracked entity may predate acquisition of the lock (e.g. callers in the same request).
                await db.Entry(row).ReloadAsync();
                RequireEditable(row, user);
                if (row.Revision != input.Conversations.Single(x => x.Id == row.Id).ExpectedRevision)
                    return Results.Conflict(new { title = "La conversación cambió. Actualiza la selección antes de asignar." });
            }
            var names = await db.Members.ToDictionaryAsync(x => x.Subject, x => x.Name);
            foreach (var row in conversations)
            {
                var previous = row.AssignedTo is {} owner ? names.GetValueOrDefault(owner, "Miembro anterior") : "Sin asignar";
                row.AssignedTo = target?.Subject;
                row.Status = row.State == "resolved" ? "closed" : "human";
                row.Revision++; row.UpdatedAt = DateTimeOffset.UtcNow;
                db.Activities.Add(InboxWorkflow.Event(scope, row, user, "assignment", $"Responsable: {previous} → {target?.Name ?? "Sin asignar"}."));
                CrmEndpoints.Audit(db, scope, user, "conversation.assigned", row.Id);
            }
            // One SaveChanges transaction: either the entire selection changes, or none does.
            await db.SaveChangesAsync();
            return Results.Ok(new { assigned = conversations.Count });
        }
        finally { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); }
    }
}
