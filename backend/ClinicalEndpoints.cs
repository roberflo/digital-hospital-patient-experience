using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;

namespace Recepcion;

public static class ClinicalEndpoints
{
    public static void RequireDoctor(CurrentUser user, Conversation conversation)
    {
        if (user.Role != "doctor" || conversation.AssignedTo != user.Subject || conversation.Status == "agent")
            throw new AccessDeniedException();
    }

    public static void MapClinical(this WebApplication app)
    {
        var routes = app.MapGroup("/api/hospital/conversations/{id:guid}/clinical").RequireAuthorization();
        routes.MapGet("/{section}", (Guid id, string section, string? cursor, HttpContext ctx, CrmDb db, TenantScope t, CurrentUser u, HospitalClinicalClient hospital) =>
            Read(id, section, null, cursor, ctx, db, t, u, hospital));
        routes.MapGet("/{section}/{documentId:guid}", (Guid id, string section, Guid documentId, HttpContext ctx, CrmDb db, TenantScope t, CurrentUser u, HospitalClinicalClient hospital) =>
            Read(id, section, documentId, null, ctx, db, t, u, hospital));
    }

    static async Task<IResult> Read(Guid id, string section, Guid? documentId, string? cursor, HttpContext ctx,
        CrmDb db, TenantScope tenant, CurrentUser user, HospitalClinicalClient hospital)
    {
        if (user.Role != "doctor") throw new AccessDeniedException();
        var conversation = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id);
        if (conversation is null) return Results.NotFound();
        RequireDoctor(user, conversation);
        var contact = await db.Contacts.AsNoTracking().SingleAsync(c => c.Id == conversation.ContactId);
        if (contact.PatientId is null) return Results.Conflict(new { title = "Vincula primero el paciente con Hospital." });
        if (!AuthenticationHeaderValue.TryParse(ctx.Request.Headers.Authorization, out var auth)
            || !auth.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(auth.Parameter))
            throw new AccessDeniedException();
        var result = await hospital.ReadAsync(tenant.Id, user.Subject, auth.Parameter, contact.PatientId.Value,
            contact.Phone, section, documentId, cursor, ctx.RequestAborted);
        // Recheck assignment/link after the external call; a transfer must revoke future reads.
        var stillAssigned = await db.Conversations.AsNoTracking().AnyAsync(c => c.Id == id && c.AssignedTo == user.Subject && c.Status != "agent");
        var currentContact = await db.Contacts.AsNoTracking().SingleOrDefaultAsync(c => c.Id == contact.Id);
        var stillLinked = currentContact?.PatientId == contact.PatientId && currentContact?.Phone == contact.Phone;
        if (!stillAssigned || !stillLinked) throw new AccessDeniedException();
        CrmEndpoints.Audit(db, tenant, user, "clinical." + section + ".read", documentId ?? contact.PatientId.Value);
        db.Activities.Add(new Activity { TenantId = tenant.Id, ContactId = contact.Id, ConversationId = id,
            Actor = user.Name, ActorRole = user.Role, ActorSubject = user.Subject, Kind = "clinical_review",
            Body = "Consultó Hospital durante la atención. El contenido clínico permanece en el expediente." });
        await db.SaveChangesAsync();
        return Results.Ok(result);
    }
}
