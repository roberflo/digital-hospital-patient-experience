using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

public static class HospitalEndpoints
{
    static IResult LegacyClinical(CurrentUser u)
    {
        if (u.Role != "doctor") throw new AccessDeniedException();
        return Results.Json(new { title = "Consulta expediente y recetas desde una conversación asignada." }, statusCode: 410);
    }
    public static void MapHospital(this WebApplication app)
    {
        var api = app.MapGroup("/api/hospital").RequireAuthorization();
        api.MapGet("/contacts/{id:guid}/appointments", ContactAppointments);
        api.MapGet("/agenda", Agenda);
        api.MapGet("/availability", Availability);
        // Staff reads moved to the conversation-scoped doctor's identity. Bot delivery stays private.
        api.MapGet("/contacts/{id:guid}/prescriptions", (CurrentUser u) => LegacyClinical(u));
        api.MapGet("/contacts/{id:guid}/prescriptions/{prescriptionId:guid}", (CurrentUser u) => LegacyClinical(u));
        api.MapGet("/contacts/{id:guid}/visit-kind", VisitKind);
        api.MapPost("/appointments", ChangeAppointment);
    }
    /// <summary>The visit kind the team's form proposes for a new appointment of this contact (MR-42).</summary>
    public static async Task<IResult> VisitKind(Guid id, DateTimeOffset? before, HttpContext ctx, CrmDb db, HospitalClient h, TenantScope t)
    {
        var contact = await db.Contacts.SingleOrDefaultAsync(x => x.Id == id); if (contact?.PatientId is null) return Results.NotFound(new { title = "Vincula primero el paciente del hospital" });
        return Results.Ok(new VisitKindAnswer(await h.AsUser(Identity.Bearer(ctx)).FirstOrFollowUpAsync(t.Id, contact.PatientId.Value, before ?? DateTimeOffset.UtcNow)));
    }

    public static async Task<IResult> ContactAppointments(Guid id, DateOnly from, DateOnly to, HttpContext ctx, CrmDb db, HospitalClient h, TenantScope t)
    {
        var contact=await db.Contacts.SingleOrDefaultAsync(x=>x.Id==id);if(contact?.PatientId is null)return Results.NotFound(new{title="Vincula primero el paciente del hospital"});
        return Results.Ok(await h.AsUser(Identity.Bearer(ctx)).GetPatientAppointmentRangeAsync(t.Id,contact.PatientId.Value,contact.Phone,from,to));
    }
    public static Task<JsonElement> Agenda(DateOnly date, HttpContext ctx, HospitalClient h, TenantScope t) => h.AsUser(Identity.Bearer(ctx)).GetAgendaDayAsync(t.Id, date);
    public static Task<HospitalBookingOptions> Availability(DateOnly date, Guid? doctorId, HttpContext ctx, HospitalClient h, TenantScope t) => h.AsUser(Identity.Bearer(ctx)).GetAvailabilityAsync(t.Id, date, date, doctorId);
    public static async Task<IResult> ChangeAppointment(AppointmentInput input, HttpContext ctx, CrmDb db, HospitalClient h, TenantScope t, CurrentUser u)
    {
        h = h.AsUser(Identity.Bearer(ctx));
        var contact = await db.Contacts.SingleOrDefaultAsync(x => x.Id == input.ContactId); if (contact?.PatientId is null) return Results.BadRequest(new { title = "Vincula el contacto al expediente del hospital" });
        if(input.Action is not("create" or "reschedule" or "cancel"))throw new ArgumentException("Acción inválida");
        if(input.Action!="create"&&input.AppointmentId is null)throw new ArgumentException("Cita requerida");
        // Refused before the idempotency receipt is written: a cancellation with no stated reason must not spend the key.
        if(input.Action=="cancel")AppointmentCancellation.From(input.CancelReason);
        // MR-42: the kind the team chose, among those Hospital publishes; none chosen, the agent's rule. Read before the key is spent.
        var visit = input.Action != "create" ? null : string.IsNullOrWhiteSpace(input.VisitKind) ? null : HospitalClient.VisitKinds.Contains(input.VisitKind) ? input.VisitKind : throw new ArgumentException("Tipo de visita inválido");
        if(input.Action!="cancel"){
            var zone=await db.Tenants.Where(x=>x.Id==t.Id).Select(x=>x.TimeZone).SingleAsync();
            if(!await h.IsSlotAvailableAsync(t.Id,input.DoctorId,input.StartsAt,input.DurationMinutes,zone))return Results.Conflict(new{title="El horario ya no está disponible"});
        }
        if (input.Action == "create") visit ??= await h.FirstOrFollowUpAsync(t.Id, contact.PatientId.Value, input.StartsAt);
        var key = Rules.Required(ctx.Request.Headers["Idempotency-Key"].ToString(), 100);
        if (await db.Receipts.AnyAsync(x => x.Key == "appointment:" + key)) return Results.Conflict(new { title = "Solicitud ya procesada. Comprueba la agenda antes de repetir." });
        var receipt = new Receipt { TenantId = t.Id, Key = "appointment:" + key };
        db.Receipts.Add(receipt); CrmEndpoints.Audit(db, t, u, "appointment.requested", contact.Id); await db.SaveChangesAsync();
        try { return await Apply(input, db, h, t, u, contact, contact.PatientId.Value, visit); }
        // Hospital refused the person (401/403), or their token was refused before it was sent: nothing was applied, so the key is not spent.
        catch (HospitalIntegrationException e) when (e.Code is "hospital.user_forbidden" or "hospital.user_token_rejected")
        { db.Receipts.Remove(receipt); await db.SaveChangesAsync(); throw; }
    }
    static async Task<IResult> Apply(AppointmentInput input, CrmDb db, HospitalClient h, TenantScope t, CurrentUser u, Contact contact, Guid patientId, string? visit)
    {
        void Record(string text){db.Activities.Add(new Activity{TenantId=t.Id,ContactId=contact.Id,Kind="appointment",Actor=u.Name,ActorRole=u.Role,ActorSubject=u.Subject,Body=text});}
        if (input.Action == "cancel")
        {
            var cancellation = AppointmentCancellation.From(input.CancelReason);
            await h.CancelAppointmentAsync(t.Id, patientId, contact.Phone, input.AppointmentId ?? throw new ArgumentException("Cita requerida"), reason: cancellation.Reason); Record(cancellation.CancelledByPatient ? "Cita cancelada en Hospital a petición del paciente." : "Cita cancelada en Hospital por el hospital.");await db.SaveChangesAsync();return Results.Ok(new { status = "cancelled" });
        }
        if (input.Action == "reschedule")
        {
            await h.RescheduleAppointmentAsync(t.Id, patientId, contact.Phone, input.AppointmentId ?? throw new ArgumentException("Cita requerida"), input.DoctorId, input.StartsAt, input.DurationMinutes); Record("Cita reprogramada en Hospital.");await db.SaveChangesAsync();return Results.Ok(new { status = "rescheduled" });
        }
        var result=await h.CreateAppointmentAsync(t.Id, contact.Phone, new(patientId, input.DoctorId, input.StartsAt, input.DurationMinutes, visit!));
        Record(result.Overlaps?"Cita registrada en Hospital con solapamiento; requiere revisión.":"Cita registrada en Hospital.");await db.SaveChangesAsync();return Results.Ok(result);
    }
}
public sealed record VisitKindAnswer(string VisitKind);
public record AppointmentInput(Guid ContactId, string Action, Guid? AppointmentId, Guid DoctorId, DateTimeOffset StartsAt, int DurationMinutes, string? CancelReason = null, string? VisitKind = null);
