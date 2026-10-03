using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;
public static class HospitalEndpoints {
    public static void MapHospital(this WebApplication app){
        var api=app.MapGroup("/api/hospital").RequireAuthorization();
        api.MapGet("/agenda",async(DateOnly date,HospitalClient h,TenantScope t)=>await h.GetAgendaDayAsync(t.Id,date));
        api.MapGet("/availability",async(DateOnly date,Guid? doctorId,HospitalClient h,TenantScope t)=>await h.GetAvailabilityAsync(t.Id,date,date,doctorId));
        api.MapGet("/contacts/{id:guid}/prescriptions",async(Guid id,CrmDb db,HospitalClient h,TenantScope t,CurrentUser u)=>{
            if(u.Role!="doctor")throw new AccessDeniedException();
            var c=await db.Contacts.SingleOrDefaultAsync(x=>x.Id==id);if(c?.PatientId is null)return Results.NotFound(new{title="Vincula primero el expediente del paciente"});
            return Results.Ok(await h.ListIssuedPrescriptionsAsync(t.Id,c.PatientId.Value,c.Phone));
        });
        api.MapGet("/contacts/{id:guid}/prescriptions/{prescriptionId:guid}",async(Guid id,Guid prescriptionId,CrmDb db,HospitalClient h,TenantScope t,CurrentUser u)=>{
            var c=await db.Contacts.SingleOrDefaultAsync(x=>x.Id==id);if(c?.PatientId is null)return Results.NotFound();
            if(u.Role!="doctor")throw new AccessDeniedException();
            var pdf=await h.GetPrescriptionPdfAsync(t.Id,c.PatientId.Value,c.Phone,prescriptionId);CrmEndpoints.Audit(db,t,u,"prescription.downloaded",prescriptionId);await db.SaveChangesAsync();return Results.File(pdf,"application/pdf","receta.pdf");
        });
        api.MapPost("/appointments",async(AppointmentInput input,HttpContext ctx,CrmDb db,HospitalClient h,TenantScope t,CurrentUser u)=>{
            var contact=await db.Contacts.SingleOrDefaultAsync(x=>x.Id==input.ContactId);if(contact?.PatientId is null)return Results.BadRequest(new{title="Vincula el contacto al expediente del hospital"});
            var key=Rules.Required(ctx.Request.Headers["Idempotency-Key"].ToString(),100);
            if(await db.Receipts.AnyAsync(x=>x.Key=="appointment:"+key))return Results.Conflict(new{title="Solicitud ya procesada. Comprueba la agenda antes de repetir."});
            db.Receipts.Add(new Receipt{TenantId=t.Id,Key="appointment:"+key});CrmEndpoints.Audit(db,t,u,"appointment.requested",contact.Id);await db.SaveChangesAsync();
            if(input.Action=="cancel"){
                await h.CancelAppointmentAsync(t.Id,contact.PatientId.Value,contact.Phone,input.AppointmentId??throw new ArgumentException("Cita requerida"));return Results.Ok(new{status="cancelled"});
            }
            if(input.Action is not("create" or "reschedule"))throw new ArgumentException("Acción inválida");
            var day=DateOnly.FromDateTime(input.StartsAt.Date);var options=await h.GetAvailabilityAsync(t.Id,day,day,input.DoctorId);
            if(!options.Professionals.SelectMany(x=>x.Days).SelectMany(x=>x.Slots).Any(x=>x.StartsAt==input.StartsAt&&x.Offered&&x.TakenBy==0&&x.DurationMinutes>=input.DurationMinutes))return Results.Conflict(new{title="El horario ya no está disponible"});
            if(input.Action=="reschedule"){
                await h.RescheduleAppointmentAsync(t.Id,contact.PatientId.Value,contact.Phone,input.AppointmentId??throw new ArgumentException("Cita requerida"),input.DoctorId,input.StartsAt,input.DurationMinutes);return Results.Ok(new{status="rescheduled"});
            }
            return Results.Ok(await h.CreateAppointmentAsync(t.Id,contact.Phone,new(contact.PatientId.Value,input.DoctorId,input.StartsAt,input.DurationMinutes,"follow-up")));
        });
    }
}
public record AppointmentInput(Guid ContactId,string Action,Guid? AppointmentId,Guid DoctorId,DateTimeOffset StartsAt,int DurationMinutes);
