using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

public record ReminderSettingsInput(bool Enabled,Guid? ChannelId);
public record ReminderConsentInput(bool Enabled);
public static class ReminderEndpoints
{
    public static void MapReminderEndpoints(this WebApplication app)
    {
        var api=app.MapGroup("/api/appointment-reminders").RequireAuthorization();
        api.MapGet("",async(CrmDb db,CurrentUser user,TenantScope scope,IConfiguration config)=>{
            user.RequireAdmin();var t=await db.Tenants.SingleAsync(t=>t.Id==scope.Id);
            var rows=await db.AppointmentReminders.OrderByDescending(r=>r.CreatedAt).Take(100).ToListAsync();
            var ids=rows.Select(r=>r.ContactId).Distinct().ToArray();var contacts=await db.Contacts.Where(c=>ids.Contains(c.Id)).ToDictionaryAsync(c=>c.Id);
            var messageIds=rows.Select(r=>r.MessageId).OfType<Guid>().ToArray();var deliveries=await db.Messages.Where(m=>messageIds.Contains(m.Id)).ToDictionaryAsync(m=>m.Id,m=>m.Status);
            return Results.Ok(new{enabled=t.RemindersEnabled,channelId=t.ReminderChannelId,dayTemplate=t.ReminderDayTemplate,hourTemplate=t.ReminderHourTemplate,language=t.ReminderLanguage,
                sendEnabled=config["REMINDERS_SEND_ENABLED"]=="true",lastSyncAt=t.ReminderLastSyncAt,error=t.ReminderSyncError,
                consentingContacts=await db.Contacts.CountAsync(c=>c.PatientId!=null&&c.ReminderConsentAt!=null),
                rows=rows.Select(r=>new{r.Id,r.AppointmentId,r.ContactId,patientName=contacts.GetValueOrDefault(r.ContactId)?.Name,r.StartsAt,r.DueAt,r.Window,r.Status,r.Reason,deliveryStatus=r.MessageId is {} m?deliveries.GetValueOrDefault(m):null})});
        });
        api.MapPut("/settings",async(ReminderSettingsInput input,CrmDb db,CurrentUser user,TenantScope scope,ConversationService service)=>{
            user.RequireAdmin();using var lease=await service.Lock(ReminderRules.LockId(scope.Id));
            if(input.ChannelId is {} id&&!await db.Channels.AnyAsync(c=>c.Id==id&&c.Enabled&&c.DoctorId==null))throw new ArgumentException("Selecciona un número general activo de este hospital.");
            if(input.Enabled&&input.ChannelId is null)throw new ArgumentException("Selecciona el número para recordatorios.");
            var tenant=await db.Tenants.SingleAsync(t=>t.Id==scope.Id);tenant.RemindersEnabled=input.Enabled;tenant.ReminderChannelId=input.ChannelId;
            CrmEndpoints.Audit(db,scope,user,"appointment_reminders.configured",scope.Id);await db.SaveChangesAsync();return Results.Ok();
        });
        api.MapGet("/templates",async(CrmDb db,CurrentUser user,TenantScope scope,KapsoClient kapso,CancellationToken ct)=>{
            user.RequireAdmin();var t=await db.Tenants.SingleAsync(t=>t.Id==scope.Id,ct);
            var ch=await db.Channels.SingleOrDefaultAsync(c=>c.Id==t.ReminderChannelId,ct);if(ch is null)return Results.BadRequest(new{title="Guarda primero el número de recordatorios."});
            var rows=new List<object>();foreach(var name in new[]{t.ReminderDayTemplate,t.ReminderHourTemplate}){
                var template=await kapso.ReminderTemplate(ch.PhoneNumberId,name,t.ReminderLanguage,ct);
                rows.Add(new{name,status=template.ValueKind==JsonValueKind.Object?template.GetProperty("status").GetString():"NOT_FOUND",ready=KapsoClient.ValidReminderTemplate(template)});
            }
            return Results.Ok(rows);
        });
        api.MapPost("/sync",async(CurrentUser user,AppointmentReminderService service,CancellationToken ct)=>{user.RequireAdmin();await service.Sync(DateTimeOffset.UtcNow,ct);return Results.Ok(new{synced=true});});
        api.MapGet("/contacts/{id:guid}",async(Guid id,CrmDb db)=>{
            var contact=await db.Contacts.SingleOrDefaultAsync(c=>c.Id==id);return contact is null?Results.NotFound():Results.Ok(new{enabled=contact.ReminderConsentAt!=null,consentAt=contact.ReminderConsentAt,linked=contact.PatientId!=null});
        });
        api.MapPut("/contacts/{id:guid}",async(Guid id,ReminderConsentInput input,CrmDb db,TenantScope scope,CurrentUser user,ConversationService service)=>{
            using var lease=await service.Lock(ReminderRules.LockId(scope.Id));
            var contact=await db.Contacts.SingleOrDefaultAsync(c=>c.Id==id);if(contact is null)return Results.NotFound();
            if(input.Enabled&&contact.PatientId is null)throw new ArgumentException("Vincula el expediente antes de autorizar recordatorios.");
            contact.ReminderConsentAt=input.Enabled?DateTimeOffset.UtcNow:null;
            if(!input.Enabled)await db.AppointmentReminders.Where(r=>r.ContactId==id&&r.Status=="pending").ExecuteUpdateAsync(s=>s.SetProperty(r=>r.Status,"cancelled").SetProperty(r=>r.Reason,"Autorización retirada."));
            db.Activities.Add(new(){TenantId=scope.Id,ContactId=id,Actor=user.Name,ActorRole=user.Role,ActorSubject=user.Subject,Kind="reminder_consent",Body=input.Enabled?"Registró la autorización del paciente para recordatorios de citas.":"Retiró la autorización de recordatorios del paciente."});
            CrmEndpoints.Audit(db,scope,user,"appointment_reminders.consent",id);await db.SaveChangesAsync();return Results.Ok();
        });
    }
}
