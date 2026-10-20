using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

public record ReminderSettingsInput(bool Enabled,Guid? ChannelId,string? DayTemplate=null);
public record ReminderConsentInput(bool Enabled);
/// <summary>One line of «Recordatorios de mañana»: hour, contact name and state only (criterios 14, 16).</summary>
public record TomorrowReminder(Guid Id,DateTimeOffset StartsAt,string? ContactName,string Status,string? DeliveryStatus,Guid? ConversationId);
public static class ReminderEndpoints
{
    public static async Task<IReadOnlyList<TomorrowReminder>> Tomorrow(CrmDb db,CurrentUser user,TenantScope scope,DateTimeOffset now,CancellationToken ct=default)
    {
        CommercialService.RequireOperator(user); // administrator and reception, never a doctor (A11)
        var tenant=await db.Tenants.AsNoTracking().SingleAsync(t=>t.Id==scope.Id,ct);var zone=TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone);
        var day=HospitalClient.ClinicalDay(now,tenant.TimeZone).AddDays(1);
        DateTimeOffset Midnight(DateOnly d)=>new(TimeZoneInfo.ConvertTimeToUtc(d.ToDateTime(TimeOnly.MinValue),zone),TimeSpan.Zero);
        var (from,to)=(Midnight(day),Midnight(day.AddDays(1)));
        var rows=await db.AppointmentReminders.AsNoTracking().Where(r=>r.Window=="day_before"&&r.StartsAt>=from&&r.StartsAt<to).OrderBy(r=>r.StartsAt).ToListAsync(ct);
        var contactIds=rows.Select(r=>r.ContactId).Distinct().ToArray();
        var names=await db.Contacts.AsNoTracking().Where(c=>contactIds.Contains(c.Id)).ToDictionaryAsync(c=>c.Id,c=>c.Name,ct);
        var conversations=await db.Conversations.AsNoTracking().Where(c=>contactIds.Contains(c.ContactId)).Select(c=>new{c.Id,c.ContactId,c.ChannelId}).ToListAsync(ct);
        var messageIds=rows.Select(r=>r.MessageId).OfType<Guid>().ToArray();
        var deliveries=await db.Messages.AsNoTracking().Where(m=>messageIds.Contains(m.Id)).ToDictionaryAsync(m=>m.Id,m=>m.Status,ct);
        return rows.Select(r=>new TomorrowReminder(r.Id,r.StartsAt,names.GetValueOrDefault(r.ContactId),r.Status,r.MessageId is {} m?deliveries.GetValueOrDefault(m):null,
            conversations.FirstOrDefault(c=>c.ContactId==r.ContactId&&c.ChannelId==r.ChannelId)?.Id)).ToList();
    }
    /// <summary>The administrator's reminder settings: channel, pause and the day-before template (criterio 13). Audited.</summary>
    public static async Task Configure(ReminderSettingsInput input,CrmDb db,CurrentUser user,TenantScope scope,ConversationService service)
    {
        user.RequireAdmin();using var lease=await service.Lock(ReminderRules.LockId(scope.Id));
        if(input.ChannelId is {} id&&!await db.Channels.AnyAsync(c=>c.Id==id&&c.Enabled&&c.DoctorId==null))throw new ArgumentException("Selecciona un número general activo de este hospital.");
        if(input.Enabled&&input.ChannelId is null)throw new ArgumentException("Selecciona el número para recordatorios.");
        // A Meta template name; whether it is approved and has the right shape is checked at dispatch (criterio 13).
        if(input.DayTemplate is {} day&&!System.Text.RegularExpressions.Regex.IsMatch(day,"^[a-z0-9_]{1,512}$"))throw new ArgumentException("Nombre de plantilla inválido.");
        var tenant=await db.Tenants.SingleAsync(t=>t.Id==scope.Id);tenant.RemindersEnabled=input.Enabled;tenant.ReminderChannelId=input.ChannelId;
        if(input.DayTemplate is {} chosen)tenant.ReminderDayTemplate=chosen;
        CrmEndpoints.Audit(db,scope,user,"appointment_reminders.configured",scope.Id);await db.SaveChangesAsync();
    }
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
        api.MapPut("/settings",async(ReminderSettingsInput input,CrmDb db,CurrentUser user,TenantScope scope,ConversationService service)=>{await Configure(input,db,user,scope,service);return Results.Ok();});
        api.MapGet("/templates",async(CrmDb db,CurrentUser user,TenantScope scope,KapsoClient kapso,CancellationToken ct)=>{
            user.RequireAdmin();var t=await db.Tenants.SingleAsync(t=>t.Id==scope.Id,ct);
            var ch=await db.Channels.SingleOrDefaultAsync(c=>c.Id==t.ReminderChannelId,ct);if(ch is null)return Results.BadRequest(new{title="Guarda primero el número de recordatorios."});
            var rows=new List<object>();foreach(var (name,window) in new[]{(t.ReminderDayTemplate,"day_before"),(t.ReminderHourTemplate,"hour_before")}){
                var template=await kapso.ReminderTemplate(ch.PhoneNumberId,name,t.ReminderLanguage,ct);
                rows.Add(new{name,status=template.ValueKind==JsonValueKind.Object?template.GetProperty("status").GetString():"NOT_FOUND",ready=KapsoClient.ValidReminderTemplate(template,window)});
            }
            return Results.Ok(rows);
        });
        api.MapGet("/tomorrow",(CrmDb db,CurrentUser user,TenantScope scope,CancellationToken ct)=>Tomorrow(db,user,scope,DateTimeOffset.UtcNow,ct));
        api.MapPost("/sync",async(HttpContext ctx,CurrentUser user,AppointmentReminderService service,CancellationToken ct)=>{user.RequireAdmin();await service.AsUser(Identity.Bearer(ctx)).SyncAsPerson(DateTimeOffset.UtcNow,ct);return Results.Ok(new{synced=true});});
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
