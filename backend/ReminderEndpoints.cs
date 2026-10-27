using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

public record ReminderSettingsInput(bool Enabled,Guid? ChannelId,string? DayTemplate=null,string? HourTemplate=null);
public record ReminderConsentInput(bool Enabled);
/// <summary>One line of «Recordatorios de mañana»: hour, contact name and state only (criterios 14, 16).</summary>
/// <param name="ChangeRequested">The patient tapped «Cambiar fecha» or «Cancelar» on this reminder and nobody answered for the agent: the desk calls.</param>
public record TomorrowReminder(Guid Id,DateTimeOffset StartsAt,string? ContactName,string Status,string? DeliveryStatus,Guid? ConversationId,bool ChangeRequested=false);
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
        var contactIds=rows.Select(r=>r.ContactId).OfType<Guid>().Distinct().ToArray();
        var names=await db.Contacts.AsNoTracking().Where(c=>contactIds.Contains(c.Id)).ToDictionaryAsync(c=>c.Id,c=>c.Name,ct);
        var conversations=await db.Conversations.AsNoTracking().Where(c=>contactIds.Contains(c.ContactId)).Select(c=>new{c.Id,c.ContactId,c.ChannelId}).ToListAsync(ct);
        var messageIds=rows.Select(r=>r.MessageId).OfType<Guid>().ToArray();
        var deliveries=await db.Messages.AsNoTracking().Where(m=>messageIds.Contains(m.Id)).ToDictionaryAsync(m=>m.Id,m=>m.Status,ct);
        var conversationIds=conversations.Select(c=>(Guid?)c.Id).ToArray();
        // A request is the tapped reminder's (its MessageId), and a person answering in the conversation afterwards settles it.
        var requests=await db.Activities.AsNoTracking().Where(a=>a.Kind=="reminder_change_request"&&conversationIds.Contains(a.ConversationId)
            &&!db.Messages.Any(m=>m.ConversationId==a.ConversationId&&m.Sender=="human"&&m.CreatedAt>a.CreatedAt)).Select(a=>new{a.ConversationId,a.MessageId,a.CreatedAt}).ToListAsync(ct);
        return rows.Select(r=>{
            var conversation=conversations.FirstOrDefault(c=>c.ContactId==r.ContactId&&c.ChannelId==r.ChannelId)?.Id;
            var since=r.AttemptedAt??r.CreatedAt;
            return new TomorrowReminder(r.Id,r.StartsAt,r.ContactId is {} contact?names.GetValueOrDefault(contact):null,r.Status,r.MessageId is {} m?deliveries.GetValueOrDefault(m):null,
                conversation,r.Status is "sent" or "confirmed" or "uncertain"&&requests.Any(a=>a.ConversationId==conversation&&(a.MessageId==null/* before the tapped reminder was kept */||a.MessageId==r.MessageId)&&a.CreatedAt>=since));
        }).ToList();
    }
    /// <summary>The administrator's reminder settings: channel, pause and the templates of both windows (criterios 13 y 52). Audited with the
    /// templates chosen (names only). Kapso is asked before the tenant's reminder lock, with the request's token (review minor 3).</summary>
    public static async Task Configure(ReminderSettingsInput input,CrmDb db,CurrentUser user,TenantScope scope,ConversationService service,KapsoClient kapso,CancellationToken ct=default)
    {
        user.RequireAdmin();
        if(input.ChannelId is {} id&&!await db.Channels.AnyAsync(c=>c.Id==id&&c.Enabled&&c.DoctorId==null,ct))throw new ArgumentException("Selecciona un número general activo de este hospital.");
        if(input.Enabled&&input.ChannelId is null)throw new ArgumentException("Selecciona el número para recordatorios.");
        // A Meta template name (criterio 13).
        foreach(var name in new[]{input.DayTemplate,input.HourTemplate})
            if(name is not null&&!System.Text.RegularExpressions.Regex.IsMatch(name,"^[a-z0-9_]{1,512}$"))throw new ArgumentException("Nombre de plantilla inválido.");
        // S5, criterio 52 (A-S5-4): a template that changes is taken only if Meta reports it APPROVED with its window's shape, before anything is written.
        var before=await db.Tenants.AsNoTracking().SingleAsync(t=>t.Id==scope.Id,ct);
        var asked=new HashSet<string>();
        foreach(var (name,current,window) in new[]{(input.DayTemplate,before.ReminderDayTemplate,"day_before"),(input.HourTemplate,before.ReminderHourTemplate,"hour_before")})
        {
            if(name is null||name==current)continue;
            var channel=input.ChannelId is {} c?await db.Channels.AsNoTracking().SingleAsync(x=>x.Id==c,ct):throw new ArgumentException("Selecciona el número para recordatorios.");
            var template=await kapso.ReminderTemplate(channel.PhoneNumberId,name,before.ReminderLanguage,ct);
            if(template.ValueKind!=JsonValueKind.Object)throw new ArgumentException($"La plantilla {name} no existe en Meta para el idioma {before.ReminderLanguage}.");
            if(!KapsoClient.ValidReminderTemplate(template,window))throw new ArgumentException($"La plantilla {name} aún no está aprobada en Meta.");
            asked.Add(window);
        }
        using var lease=await service.Lock(ReminderRules.LockId(scope.Id),ct);
        var tenant=await db.Tenants.SingleAsync(t=>t.Id==scope.Id,ct);
        // Someone changed the templates while Kapso answered: what was checked no longer says what changes.
        if(tenant.ReminderLanguage!=before.ReminderLanguage
            ||input.DayTemplate is {} d&&d!=tenant.ReminderDayTemplate&&!asked.Contains("day_before")||input.HourTemplate is {} h&&h!=tenant.ReminderHourTemplate&&!asked.Contains("hour_before"))
            throw new ArgumentException("La configuración de recordatorios cambió. Actualiza e inténtalo de nuevo.");
        tenant.RemindersEnabled=input.Enabled;tenant.ReminderChannelId=input.ChannelId;
        if(input.DayTemplate is {} day)tenant.ReminderDayTemplate=day;
        if(input.HourTemplate is {} hour)tenant.ReminderHourTemplate=hour;
        db.Audits.Add(new Audit{TenantId=scope.Id,Actor=user.Subject,Action="appointment_reminders.configured",Resource=$"day={tenant.ReminderDayTemplate};hour={tenant.ReminderHourTemplate}"});
        await db.SaveChangesAsync(ct);
    }
    public static void MapReminderEndpoints(this WebApplication app)
    {
        var api=app.MapGroup("/api/appointment-reminders").RequireAuthorization();
        api.MapGet("",async(CrmDb db,CurrentUser user,TenantScope scope,IConfiguration config)=>{
            user.RequireAdmin();var t=await db.Tenants.SingleAsync(t=>t.Id==scope.Id);
            var rows=await db.AppointmentReminders.OrderByDescending(r=>r.CreatedAt).Take(100).ToListAsync();
            var ids=rows.Select(r=>r.ContactId).OfType<Guid>().Distinct().ToArray();var contacts=await db.Contacts.Where(c=>ids.Contains(c.Id)).ToDictionaryAsync(c=>c.Id);
            var messageIds=rows.Select(r=>r.MessageId).OfType<Guid>().ToArray();var deliveries=await db.Messages.Where(m=>messageIds.Contains(m.Id)).ToDictionaryAsync(m=>m.Id,m=>m.Status);
            return Results.Ok(new{enabled=t.RemindersEnabled,channelId=t.ReminderChannelId,dayTemplate=t.ReminderDayTemplate,hourTemplate=t.ReminderHourTemplate,language=t.ReminderLanguage,
                sendEnabled=config["REMINDERS_SEND_ENABLED"]=="true",lastSyncAt=t.ReminderLastSyncAt,error=t.ReminderSyncError,
                consentingContacts=await db.Contacts.CountAsync(c=>c.PatientId!=null&&c.ReminderConsentAt!=null),
                rows=rows.Select(r=>new{r.Id,r.AppointmentId,r.ContactId,patientName=r.ContactId is {} contact?contacts.GetValueOrDefault(contact)?.Name:null,r.StartsAt,r.DueAt,r.Window,r.Status,r.Reason,deliveryStatus=r.MessageId is {} m?deliveries.GetValueOrDefault(m):null})});
        });
        api.MapPut("/settings",async(ReminderSettingsInput input,CrmDb db,CurrentUser user,TenantScope scope,ConversationService service,KapsoClient kapso,CancellationToken ct)=>{await Configure(input,db,user,scope,service,kapso,ct);return Results.Ok();});
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
