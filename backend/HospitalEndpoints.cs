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
        api.MapGet("/contacts/{id:guid}/appointments",async(Guid id,DateOnly from,DateOnly to,CrmDb db,HospitalClient h,TenantScope t)=>{
            var contact=await db.Contacts.SingleOrDefaultAsync(x=>x.Id==id);if(contact?.PatientId is null)return Results.NotFound(new{title="Vincula primero el paciente del hospital"});
            return Results.Ok(await h.GetPatientAppointmentRangeAsync(t.Id,contact.PatientId.Value,contact.Phone,from,to));
        });
        api.MapGet("/agenda", async (DateOnly date, HospitalClient h, TenantScope t) => await h.GetAgendaDayAsync(t.Id, date));
        api.MapGet("/availability", async (DateOnly date, Guid? doctorId, HospitalClient h, TenantScope t) => await h.GetAvailabilityAsync(t.Id, date, date, doctorId));
        // Staff reads moved to the conversation-scoped doctor's identity. Bot delivery stays private.
        api.MapGet("/contacts/{id:guid}/prescriptions", (CurrentUser u) => LegacyClinical(u));
        api.MapGet("/contacts/{id:guid}/prescriptions/{prescriptionId:guid}", (CurrentUser u) => LegacyClinical(u));
        api.MapPost("/appointments", async (AppointmentInput input, HttpContext ctx, CrmDb db, HospitalClient h, TenantScope t, CurrentUser u) =>
        {
            var contact = await db.Contacts.SingleOrDefaultAsync(x => x.Id == input.ContactId); if (contact?.PatientId is null) return Results.BadRequest(new { title = "Vincula el contacto al expediente del hospital" });
            if(input.Action is not("create" or "reschedule" or "cancel"))throw new ArgumentException("Acción inválida");
            if(input.Action!="create"&&input.AppointmentId is null)throw new ArgumentException("Cita requerida");
            // Refused before the idempotency receipt is written: a cancellation with no stated reason must not spend the key.
            if(input.Action=="cancel")AppointmentCancellation.From(input.CancelReason);
            if(input.Action!="cancel"){
                var zone=await db.Tenants.Where(x=>x.Id==t.Id).Select(x=>x.TimeZone).SingleAsync();
                if(!await h.IsSlotAvailableAsync(t.Id,input.DoctorId,input.StartsAt,input.DurationMinutes,zone))return Results.Conflict(new{title="El horario ya no está disponible"});
            }
            var key = Rules.Required(ctx.Request.Headers["Idempotency-Key"].ToString(), 100);
            if (await db.Receipts.AnyAsync(x => x.Key == "appointment:" + key)) return Results.Conflict(new { title = "Solicitud ya procesada. Comprueba la agenda antes de repetir." });
            db.Receipts.Add(new Receipt { TenantId = t.Id, Key = "appointment:" + key }); CrmEndpoints.Audit(db, t, u, "appointment.requested", contact.Id); await db.SaveChangesAsync();
            void Record(string text){db.Activities.Add(new Activity{TenantId=t.Id,ContactId=contact.Id,Kind="appointment",Actor=u.Name,ActorRole=u.Role,ActorSubject=u.Subject,Body=text});}
            if (input.Action == "cancel")
            {
                var cancellation = AppointmentCancellation.From(input.CancelReason);
                await h.CancelAppointmentAsync(t.Id, contact.PatientId.Value, contact.Phone, input.AppointmentId ?? throw new ArgumentException("Cita requerida"), reason: cancellation.Reason); Record(cancellation.CancelledByPatient ? "Cita cancelada en Hospital a petición del paciente." : "Cita cancelada en Hospital por el hospital.");await db.SaveChangesAsync();return Results.Ok(new { status = "cancelled" });
            }
            if (input.Action == "reschedule")
            {
                await h.RescheduleAppointmentAsync(t.Id, contact.PatientId.Value, contact.Phone, input.AppointmentId ?? throw new ArgumentException("Cita requerida"), input.DoctorId, input.StartsAt, input.DurationMinutes); Record("Cita reprogramada en Hospital.");await db.SaveChangesAsync();return Results.Ok(new { status = "rescheduled" });
            }
            var result=await h.CreateAppointmentAsync(t.Id, contact.Phone, new(contact.PatientId.Value, input.DoctorId, input.StartsAt, input.DurationMinutes, "follow-up"));
            Record(result.Overlaps?"Cita registrada en Hospital con solapamiento; requiere revisión.":"Cita registrada en Hospital.");await db.SaveChangesAsync();return Results.Ok(result);
        });
    }
}
public record AppointmentInput(Guid ContactId, string Action, Guid? AppointmentId, Guid DoctorId, DateTimeOffset StartsAt, int DurationMinutes, string? CancelReason = null);
