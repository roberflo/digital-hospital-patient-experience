using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

public static class ReminderRules
{
    // PostgreSQL timestamps retain microseconds; normalize source instants before comparing snapshots.
    public static DateTimeOffset Instant(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
    public static readonly string[] Windows = ["day_before", "hour_before"];
    public static Guid LockId(Guid tenant) => new(SHA256.HashData(Encoding.UTF8.GetBytes("appointment-reminders:" + tenant)).AsSpan(0,16));
    public static DateTimeOffset Due(DateTimeOffset start, string window, string zone)
    {
        if(window == "hour_before") return start.ToUniversalTime().AddHours(-1);
        if(window != "day_before") throw new ArgumentException("Ventana inválida");
        var tz = TimeZoneInfo.FindSystemTimeZoneById(zone);
        var local = TimeZoneInfo.ConvertTime(start,tz).Date.AddDays(-1).AddHours(9);
        // Invalid/ambiguous wall times must never silently pick an arbitrary instant.
        if(tz.IsInvalidTime(local) || tz.IsAmbiguousTime(local)) throw new ArgumentException("Horario local ambiguo");
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local,tz));
    }
    public static bool Booked(string? status) => status == "booked";
    public const string DayTemplateV4 = "recepcion_cita_dia_anterior_v4";
    /// <summary>The command a «Confirmar» tap carries: <c>ASISTO {reminderId}</c>, exactly.</summary>
    public static Guid? Acknowledgement(string body) =>
        System.Text.RegularExpressions.Regex.Match(body.Trim(),"^ASISTO ([0-9a-fA-F-]{36})$") is {Success:true} m&&Guid.TryParse(m.Groups[1].Value,out var id)?id:null;
    /// <summary>Hospital still has this appointment booked at the reminder's start.</summary>
    public static bool StillBooked(JsonElement agenda,AppointmentReminder row) =>
        agenda.GetProperty("rows").EnumerateArray().Any(a=>a.GetProperty("appointmentId").GetGuid()==row.AppointmentId&&Instant(a.GetProperty("scheduledStart").GetDateTimeOffset())==row.StartsAt&&Booked(a.GetProperty("status").GetString()));
    public static (string Date,string Time) Local(DateTimeOffset start,string zone)
    {
        var local=TimeZoneInfo.ConvertTime(start,TimeZoneInfo.FindSystemTimeZoneById(zone));
        return (local.ToString("dd/MM/yyyy",CultureInfo.InvariantCulture),local.ToString("HH:mm",CultureInfo.InvariantCulture));
    }
    /// <summary>Payloads of the day-before buttons (Confirmar, Cambiar fecha, Cancelar): verb and the id of the reminder sent, nothing else.
    /// The reminder, not the appointment, so a tap answers exactly that send: after a move, the old reminder no longer confirms the new start (owner, I-1).</summary>
    public static string[] Payloads(Guid reminder) => [$"ASISTO {reminder}", $"MOVER {reminder}", $"CANCELAR {reminder}"];
    public static string? ConsentCommand(string text) => text.Trim().ToUpperInvariant() switch
    {
        "BAJA" or "STOP" or "CANCELAR RECORDATORIOS" => "off", "ACTIVAR RECORDATORIOS" => "on", _ => null
    };
}

public enum ReminderAck { Confirmed, AlreadyConfirmed, Rejected, Unverified }

public sealed partial class AppointmentReminderService(CrmDb db,TenantScope scope,HospitalClient hospital,KapsoClient kapso,ConversationService conversations,IConfiguration config)
{
    /// <summary>For a sync a person starts (MR-11): Hospital sees their token. The reminder worker never calls this.</summary>
    public AppointmentReminderService AsUser(string bearer)=>new(db,scope,hospital.AsUser(bearer),kapso,conversations,config);
    /// <summary>A sync an Administrador starts, on their own token: refused with under two minutes of it left, and bounded to 90 s so it
    /// ends while the token still works. Past the budget nothing more is read; the worker's next pass completes it.</summary>
    public async Task SyncAsPerson(DateTimeOffset now, CancellationToken ct, TimeSpan? budget = null)
    {
        hospital.RequireUserTokenFor(scope.Id, TimeSpan.FromMinutes(2));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(budget ?? TimeSpan.FromSeconds(90));
        try { await Sync(now, deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new HospitalIntegrationException("hospital.timeout", HttpStatusCode.GatewayTimeout); }
    }
    /// <param name="contactBudget">How long one contact's Hospital read may take before it counts as a failure (20 s).</param>
    public async Task Sync(DateTimeOffset now, CancellationToken ct = default, TimeSpan? contactBudget = null)
    {
        using var lease = await conversations.Lock(ReminderRules.LockId(scope.Id),ct);
        var tenant = await db.Tenants.SingleAsync(t=>t.Id==scope.Id,ct);
        if(tenant.Mode=="reminders"){await SyncGeneral(tenant,now,ct);return;} // the general number (S3), never this tenant's channels
        if(tenant.ReminderChannelId is not {} channelId) throw new ArgumentException("Selecciona el número general del hospital para los recordatorios.");
        var channel = await db.Channels.SingleOrDefaultAsync(c=>c.Id==channelId,ct);
        if(channel is null || !channel.Enabled) throw new ArgumentException("El canal de recordatorios está desactivado.");
        var day=HospitalClient.ClinicalDay(now,tenant.TimeZone);var to=day.AddDays(2);
        // ponytail: walked by Id, so a sync that spends its budget always leaves the same contacts last. Walk by last-synced-at
        // ascending if a slow Hospital makes that happen in practice (needs a per-contact sync column, i.e. a migration).
        var contacts=await db.Contacts.Where(c=>c.PatientId!=null&&c.ReminderConsentAt!=null).OrderBy(c=>c.Id).ToListAsync(ct);
        var pending=await db.AppointmentReminders.Where(r=>r.Status=="pending"||r.Status=="sending").ToListAsync(ct);
        foreach(var row in pending.Where(r=>r.Status=="sending"&&r.AttemptedAt<now.AddMinutes(-10)))
        {
            row.Status="uncertain";row.Reason="Envío interrumpido; comprueba WhatsApp antes de repetir.";
            if(row.MessageId is {} messageId)
                await db.Messages.Where(m=>m.Id==messageId&&m.Status=="sending").ExecuteUpdateAsync(s=>s.SetProperty(m=>m.Status,"uncertain"),ct);
        }
        foreach(var row in pending.Where(r=>r.Status=="pending"&&!contacts.Any(c=>c.Id==r.ContactId&&c.PatientId==r.PatientId))) { row.Status="cancelled";row.Reason="Autorización retirada o expediente desvinculado."; }
        var failures=0;
        foreach(var contact in contacts)
        {
            try
            {
                using var read=CancellationTokenSource.CreateLinkedTokenSource(ct);read.CancelAfter(contactBudget??TimeSpan.FromSeconds(20));
                var agenda=await hospital.GetPatientAppointmentRangeAsync(scope.Id,contact.PatientId!.Value,contact.Phone,day,to,read.Token);
                var rows=agenda.GetProperty("rows").EnumerateArray().Where(a=>ReminderRules.Booked(a.GetProperty("status").GetString())).ToArray();
                var valid=rows.Select(a=>(Id:a.GetProperty("appointmentId").GetGuid(),Start:ReminderRules.Instant(a.GetProperty("scheduledStart").GetDateTimeOffset()))).ToHashSet();
                foreach(var old in pending.Where(r=>r.ContactId==contact.Id&&r.Status=="pending"))
                    if(old.ChannelId!=channelId || !valid.Contains((old.AppointmentId,old.StartsAt))) {old.Status="cancelled";old.Reason="La cita cambió o ya no está programada en Hospital.";}
                foreach(var appointment in valid.Where(a=>a.Start>now))
                    foreach(var window in ReminderRules.Windows)
                    {
                        var existing=await db.AppointmentReminders.SingleOrDefaultAsync(r=>r.AppointmentId==appointment.Id&&r.StartsAt==appointment.Start&&r.Window==window,ct);
                        if(existing is not null)
                        {
                            if(existing.Status=="cancelled"&&existing.AttemptedAt is null&&existing.ContactId==contact.Id&&existing.PatientId==contact.PatientId){existing.ChannelId=channelId;existing.Status="pending";existing.Reason=null;}
                            continue;
                        }
                        var due=ReminderRules.Due(appointment.Start,window,tenant.TimeZone);
                        db.AppointmentReminders.Add(new(){TenantId=scope.Id,ContactId=contact.Id,PatientId=contact.PatientId.Value,ChannelId=channelId,AppointmentId=appointment.Id,StartsAt=appointment.Start,DueAt=due,Window=window,
                            Status=due<now.AddMinutes(-15)?"skipped":"pending",Reason=due<now.AddMinutes(-15)?"El horario del recordatorio ya pasó.":null,CreatedAt=now});
                    }
                await db.SaveChangesAsync(ct);
            }
            // A contact that times out is one failure; only the sync's own deadline stops the others.
            // Hospital refusing the person who started the sync is not one contact's failure: it is theirs to know.
            catch(Exception ex) when(ex is HospitalIntegrationException{Code:not("hospital.user_forbidden" or "hospital.user_token_rejected")} or HttpRequestException or ArgumentException or JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            { failures++; } // No stale Hospital snapshot is used to authorize sending.
        }
        tenant.ReminderLastSyncAt=now;tenant.ReminderSyncError=failures>0?$"No se pudo verificar la agenda de {failures} contacto(s). Se volverá a consultar Hospital.":null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The patient tapped «Confirmar» on one reminder (criterios 6–7, INV-6, I-1). Accepted only for that very send: a day-before reminder
    /// of this tenant, sent to this contact, still linked to the same patient, still ahead, and re-read now in Hospital as booked at that same
    /// start. Hospital is read outside the reminders lock; a refusal or an unreachable Hospital leaves a note for the desk and nothing for the patient.</summary>
    public async Task<(ReminderAck Result,string? Date,string? Time)> Acknowledge(Guid contactId,Guid reminderId,Guid conversationId,DateTimeOffset now,CancellationToken ct=default)
    {
        var contact=await db.Contacts.AsNoTracking().SingleOrDefaultAsync(c=>c.Id==contactId,ct);
        async Task<(ReminderAck,string?,string?)> Note(ReminderAck result,string kind,string body)
        {
            db.Activities.Add(new(){TenantId=scope.Id,ContactId=contact?.Id,ConversationId=conversationId,Actor="Recordatorios",ActorRole="system",Kind=kind,Body=body});
            await db.SaveChangesAsync(ct);return (result,null,null);
        }
        Task<(ReminderAck,string?,string?)> Reject(string body="El paciente tocó «Confirmar», pero no se registró: la cita ya no está programada a esa hora en Hospital, ya pasó o no corresponde a este contacto.")=>
            Note(ReminderAck.Rejected,"appointment_confirmation_rejected",body);
        var tenant=await db.Tenants.AsNoTracking().SingleAsync(t=>t.Id==scope.Id,ct);
        var row=await db.AppointmentReminders.AsNoTracking().SingleOrDefaultAsync(r=>r.Id==reminderId&&r.Window=="day_before",ct);
        if(row is null||contact is null||row.ContactId!=contact.Id||contact.PatientId!=row.PatientId)return await Reject();
        if(row.Status=="confirmed")return (ReminderAck.AlreadyConfirmed,null,null);
        if(row.Status=="uncertain")return await Reject("El paciente tocó «Confirmar» en un recordatorio cuya entrega no se confirmó; no se registró. Confirma la cita con el paciente.");
        if(row.Status!="sent"||row.StartsAt<=now)return await Reject();
        bool booked;
        try
        {
            var day=HospitalClient.ClinicalDay(row.StartsAt,tenant.TimeZone);
            booked=ReminderRules.StillBooked(await hospital.GetPatientAppointmentRangeAsync(scope.Id,row.PatientId,contact.Phone,day,day,ct),row);
        }
        catch(HospitalIntegrationException ex) when(ex.Code=="hospital.patient_phone_mismatch") { booked=false; } // the tap is not from the patient's number
        catch(Exception ex) when(ex is HttpRequestException or HospitalIntegrationException||ex is TaskCanceledException&&!ct.IsCancellationRequested)
        { return await Note(ReminderAck.Unverified,"appointment_confirmation_unverified","Confirmación recibida, sin verificar con Hospital. Confirma la cita con el paciente."); }
        if(!booked)return await Reject();
        using(await conversations.Lock(ReminderRules.LockId(scope.Id),ct))
        {
            var fresh=await db.AppointmentReminders.SingleAsync(r=>r.Id==row.Id,ct);await db.Entry(fresh).ReloadAsync(ct);
            if(fresh.Status=="confirmed")return (ReminderAck.AlreadyConfirmed,null,null);
            if(fresh.Status!="sent"||fresh.StartsAt!=row.StartsAt)return await Reject();
            fresh.Status="confirmed";
            await Note(ReminderAck.Confirmed,"appointment_confirmed","El paciente confirmó su asistencia desde el recordatorio.");
        }
        var (date,time)=ReminderRules.Local(row.StartsAt,tenant.TimeZone);
        return (ReminderAck.Confirmed,date,time);
    }

    public async Task Dispatch(Guid id,DateTimeOffset now,CancellationToken ct=default)
    {
        using var lease=await conversations.Lock(ReminderRules.LockId(scope.Id),ct);
        var row=await db.AppointmentReminders.SingleOrDefaultAsync(r=>r.Id==id,ct);if(row is null)return;
        await db.Entry(row).ReloadAsync(ct);if(row.Status!="pending"||row.DueAt>now)return;
        var tenant=await db.Tenants.AsNoTracking().SingleAsync(t=>t.Id==scope.Id,ct);
        if(!tenant.RemindersEnabled||config["REMINDERS_SEND_ENABLED"]!="true")return;
        async Task Stop(string status,string reason){row.Status=status;row.Reason=reason;await db.SaveChangesAsync(ct);}
        if(now>row.DueAt.AddMinutes(15)||now>=row.StartsAt){await Stop("skipped","El horario del recordatorio ya pasó.");return;}
        // Criterio 18: a hospital without Recepción always sends from the general number; one with Recepción never does.
        if(tenant.Mode=="reminders"){await DispatchGeneral(row,tenant,now,ct);return;}
        var contact=await db.Contacts.AsNoTracking().SingleOrDefaultAsync(c=>c.Id==row.ContactId,ct);
        var channel=await db.Channels.AsNoTracking().SingleOrDefaultAsync(c=>c.Id==row.ChannelId,ct);
        if(contact?.PatientId!=row.PatientId||contact.ReminderConsentAt is null||channel is null||!channel.Enabled||tenant.ReminderChannelId!=row.ChannelId){await Stop("cancelled","Autorización, paciente o canal ya no disponibles.");return;}
        // Re-read the current Hospital appointment and recipient immediately before dispatch.
        var day=HospitalClient.ClinicalDay(row.StartsAt,tenant.TimeZone);
        var agenda=await hospital.GetPatientAppointmentRangeAsync(scope.Id,row.PatientId,contact.Phone,day,day,ct);
        if(!ReminderRules.StillBooked(agenda,row))
        {await Stop("cancelled","Cita cancelada o reprogramada en Hospital.");return;}
        var template=row.Window=="day_before"?tenant.ReminderDayTemplate:tenant.ReminderHourTemplate;
        if(!await kapso.ReminderTemplateApproved(channel.PhoneNumberId,template,tenant.ReminderLanguage,row.Window,ct)){row.Reason="Plantilla pendiente de aprobación o incompatible.";await db.SaveChangesAsync(ct);return;}
        // Consent edits and dispatch share this lock; external phone changes are verified above.
        var conv=await db.Conversations.SingleOrDefaultAsync(c=>c.ContactId==contact.Id&&c.ChannelId==channel.Id,ct);
        if(conv is null){conv=new(){TenantId=scope.Id,ContactId=contact.Id,ChannelId=channel.Id,Status="human"};db.Add(conv);await db.SaveChangesAsync(ct);}
        using var conversationLease=await conversations.Lock(conv.Id,ct);
        await db.Entry(conv).ReloadAsync(ct);
        var (date,time)=ReminderRules.Local(row.StartsAt,tenant.TimeZone);
        var body=$"Recordatorio de cita en {tenant.Name}, el {date} a las {time}.";
        var message=new Message{TenantId=scope.Id,ConversationId=conv.Id,Sender="system",Type="template",Body=body,Status="sending",RequestKey="reminder:"+row.Id};
        db.Add(message);row.MessageId=message.Id;row.Status="sending";row.AttemptedAt=now;row.Reason=null;
        db.Activities.Add(new(){TenantId=scope.Id,ContactId=contact.Id,ConversationId=conv.Id,MessageId=message.Id,Actor="Recordatorios",ActorRole="system",Kind="appointment_reminder",Body=row.Window=="day_before"?"Recordatorio de la cita: día anterior a las 09:00.":"Recordatorio de la cita: una hora antes."});
        await db.SaveChangesAsync(ct); // Durable attempt before external I/O, never automatically replay.
        try
        {
            message.ExternalId=(await kapso.SendReminder(channel.PhoneNumberId,contact.Phone,template,tenant.ReminderLanguage,tenant.Name,date,time,ct,row.Window=="day_before"?ReminderRules.Payloads(row.Id):null)).Id;
            message.Status="sent";row.Status="sent";
        }
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or ArgumentException)
        {
            // Kapso refusing the send (4xx) means nothing was delivered; a 5xx or timeout may have been. Neither is retried.
            var refused=ex is HttpRequestException{StatusCode:>=HttpStatusCode.BadRequest and <HttpStatusCode.InternalServerError};
            message.Status=refused||ex is ArgumentException?"failed":"uncertain";row.Status=message.Status;
            row.Reason=refused?"WhatsApp rechazó el envío; no se entregó.":"Entrega sin confirmar. Revisa WhatsApp antes de volver a enviar.";
        }
        conv.LastMessage=body;conv.UpdatedAt=now;conv.Revision++;
        await db.SaveChangesAsync(CancellationToken.None);
    }
}

public sealed class AppointmentReminderWorker(IServiceScopeFactory scopes,ILogger<AppointmentReminderWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var root=scopes.CreateScope();var db=root.ServiceProvider.GetRequiredService<CrmDb>();
                var (full,retryOnly)=await Pass(db,ct);
                foreach(var tenant in full) await RunTenant(scopes,tenant,log,ct);
                foreach(var tenant in retryOnly) await RetryTenant(scopes,tenant,log,ct);
            }
            catch(Exception ex) when(!ct.IsCancellationRequested){log.LogWarning("Reminder worker unavailable {ErrorType}",ex.GetType().Name);}
            await Task.Delay(TimeSpan.FromMinutes(1),ct);
        }
    }

    /// <summary>Who this pass serves: hospitals with reminders on get the whole pass; a hospital there only because a BAJA's withdrawal is still
    /// pending (criterio 25: whatever its mode or pause) gets only that retry, never a sync or a send. Read from the worker's root scope, which has
    /// no tenant: the pending rows are found across hospitals.</summary>
    public static async Task<(IReadOnlyList<Guid> Full,IReadOnlyList<Guid> RetryOnly)> Pass(CrmDb db,CancellationToken ct)
    {
        var full=await db.Tenants.Where(t=>t.RemindersEnabled).Select(t=>t.Id).ToListAsync(ct);
        var retry=await db.AppointmentReminders.IgnoreQueryFilters().Where(r=>r.OptOutPendingAt!=null&&!full.Contains(r.TenantId)).Select(r=>r.TenantId).Distinct().ToListAsync(ct);
        return (full,retry);
    }
    public static async Task RetryTenant(IServiceScopeFactory scopes,Guid tenant,ILogger log,CancellationToken ct)
    {
        using var s=scopes.CreateScope();s.ServiceProvider.GetRequiredService<TenantScope>().Id=tenant;
        try { await s.ServiceProvider.GetRequiredService<AppointmentReminderService>().RetryOptOuts(ct); }
        catch(Exception ex) when(!ct.IsCancellationRequested){log.LogWarning("Reminder opt-out retry unavailable {TenantId} {ErrorType}",tenant,ex.GetType().Name);}
    }
    /// <summary>One tenant's pass: sync the agenda, then send what is due. Sending has its own deadline, so a sync that spends
    /// its whole budget on a slow Hospital never holds back a reminder already due (it was verified when it was created and
    /// Dispatch re-reads Hospital before sending).</summary>
    public static async Task RunTenant(IServiceScopeFactory scopes,Guid tenant,ILogger log,CancellationToken ct,TimeSpan? syncBudget=null,TimeSpan? contactBudget=null)
    {
        using var s=scopes.CreateScope();s.ServiceProvider.GetRequiredService<TenantScope>().Id=tenant;
        var scoped=s.ServiceProvider.GetRequiredService<CrmDb>();var service=s.ServiceProvider.GetRequiredService<AppointmentReminderService>();
        try { await service.RetryOptOuts(ct); }
        catch(Exception ex) when(!ct.IsCancellationRequested){log.LogWarning("Reminder opt-out retry unavailable {TenantId} {ErrorType}",tenant,ex.GetType().Name);scoped.ChangeTracker.Clear();}
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(syncBudget??TimeSpan.FromMinutes(2));
            await service.Sync(DateTimeOffset.UtcNow,deadline.Token,contactBudget);
        }
        catch(Exception ex) when(!ct.IsCancellationRequested)
        {
            log.LogWarning("Reminder sync unavailable {TenantId} {ErrorType}",tenant,ex.GetType().Name);
            scoped.ChangeTracker.Clear(); // nothing half-synced is saved by the sends below
        }
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var due=await scoped.AppointmentReminders.Where(r=>r.Status=="pending"&&r.DueAt<=DateTimeOffset.UtcNow).OrderBy(r=>r.DueAt).Select(r=>r.Id).Take(100).ToListAsync(deadline.Token);
            foreach(var id in due)
            {
                try { await service.Dispatch(id,DateTimeOffset.UtcNow,deadline.Token); }
                catch(Exception ex) when(!deadline.IsCancellationRequested && ex is HospitalIntegrationException or HttpRequestException or ArgumentException or JsonException)
                { await scoped.AppointmentReminders.Where(r=>r.Id==id&&r.Status=="pending").ExecuteUpdateAsync(s=>s.SetProperty(r=>r.Reason,"No se pudo verificar Hospital o la plantilla. Se reintentará dentro de la ventana."),deadline.Token); }
            }
        }
        catch(Exception ex) when(!ct.IsCancellationRequested){log.LogWarning("Reminder sweep unavailable {TenantId} {ErrorType}",tenant,ex.GetType().Name);}
    }
}
