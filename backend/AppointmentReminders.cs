using System.Globalization;
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
    public static string? ConsentCommand(string text) => text.Trim().ToUpperInvariant() switch
    {
        "BAJA" or "STOP" or "CANCELAR RECORDATORIOS" => "off", "ACTIVAR RECORDATORIOS" => "on", _ => null
    };
}

public sealed class AppointmentReminderService(CrmDb db,TenantScope scope,HospitalClient hospital,KapsoClient kapso,ConversationService conversations,IConfiguration config)
{
    public async Task Sync(DateTimeOffset now, CancellationToken ct = default)
    {
        using var lease = await conversations.Lock(ReminderRules.LockId(scope.Id),ct);
        var tenant = await db.Tenants.SingleAsync(t=>t.Id==scope.Id,ct);
        if(tenant.ReminderChannelId is not {} channelId) throw new ArgumentException("Selecciona el número general del hospital para los recordatorios.");
        var channel = await db.Channels.SingleOrDefaultAsync(c=>c.Id==channelId,ct);
        if(channel is null || !channel.Enabled) throw new ArgumentException("El canal de recordatorios está desactivado.");
        var day=HospitalClient.ClinicalDay(now,tenant.TimeZone);var to=day.AddDays(2);
        var contacts=await db.Contacts.Where(c=>c.PatientId!=null&&c.ReminderConsentAt!=null).ToListAsync(ct);
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
                var agenda=await hospital.GetPatientAppointmentRangeAsync(scope.Id,contact.PatientId!.Value,contact.Phone,day,to,ct);
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
            catch(Exception ex) when(ex is HospitalIntegrationException or HttpRequestException or ArgumentException or JsonException)
            { failures++; } // No stale Hospital snapshot is used to authorize sending.
        }
        tenant.ReminderLastSyncAt=now;tenant.ReminderSyncError=failures>0?$"No se pudo verificar la agenda de {failures} contacto(s). Se volverá a consultar Hospital.":null;
        await db.SaveChangesAsync(ct);
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
        var contact=await db.Contacts.AsNoTracking().SingleOrDefaultAsync(c=>c.Id==row.ContactId,ct);
        var channel=await db.Channels.AsNoTracking().SingleOrDefaultAsync(c=>c.Id==row.ChannelId,ct);
        if(contact?.PatientId!=row.PatientId||contact.ReminderConsentAt is null||channel is null||!channel.Enabled||tenant.ReminderChannelId!=row.ChannelId){await Stop("cancelled","Autorización, paciente o canal ya no disponibles.");return;}
        // Re-read the current Hospital appointment and recipient immediately before dispatch.
        var day=HospitalClient.ClinicalDay(row.StartsAt,tenant.TimeZone);
        var agenda=await hospital.GetPatientAppointmentRangeAsync(scope.Id,row.PatientId,contact.Phone,day,day,ct);
        if(!agenda.GetProperty("rows").EnumerateArray().Any(a=>a.GetProperty("appointmentId").GetGuid()==row.AppointmentId&&ReminderRules.Instant(a.GetProperty("scheduledStart").GetDateTimeOffset())==row.StartsAt&&ReminderRules.Booked(a.GetProperty("status").GetString())))
        {await Stop("cancelled","Cita cancelada o reprogramada en Hospital.");return;}
        var template=row.Window=="day_before"?tenant.ReminderDayTemplate:tenant.ReminderHourTemplate;
        if(!await kapso.ReminderTemplateApproved(channel.PhoneNumberId,template,tenant.ReminderLanguage,ct)){row.Reason="Plantilla pendiente de aprobación o incompatible.";await db.SaveChangesAsync(ct);return;}
        // Consent edits and dispatch share this lock; external phone changes are verified above.
        var conv=await db.Conversations.SingleOrDefaultAsync(c=>c.ContactId==contact.Id&&c.ChannelId==channel.Id,ct);
        if(conv is null){conv=new(){TenantId=scope.Id,ContactId=contact.Id,ChannelId=channel.Id,Status="human"};db.Add(conv);await db.SaveChangesAsync(ct);}
        using var conversationLease=await conversations.Lock(conv.Id,ct);
        await db.Entry(conv).ReloadAsync(ct);
        var local=TimeZoneInfo.ConvertTime(row.StartsAt,TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone));
        var date=local.ToString("dd/MM/yyyy",CultureInfo.InvariantCulture);var time=local.ToString("HH:mm",CultureInfo.InvariantCulture);
        var body=$"Recordatorio de cita en {tenant.Name}, el {date} a las {time}.";
        var message=new Message{TenantId=scope.Id,ConversationId=conv.Id,Sender="system",Type="template",Body=body,Status="sending",RequestKey="reminder:"+row.Id};
        db.Add(message);row.MessageId=message.Id;row.Status="sending";row.AttemptedAt=now;row.Reason=null;
        db.Activities.Add(new(){TenantId=scope.Id,ContactId=contact.Id,ConversationId=conv.Id,MessageId=message.Id,Actor="Recordatorios",ActorRole="system",Kind="appointment_reminder",Body=row.Window=="day_before"?"Recordatorio de la cita: día anterior a las 09:00.":"Recordatorio de la cita: una hora antes."});
        await db.SaveChangesAsync(ct); // Durable attempt before external I/O, never automatically replay.
        try
        {
            message.ExternalId=await kapso.SendReminder(channel.PhoneNumberId,contact.Phone,template,tenant.ReminderLanguage,tenant.Name,date,time,ct);
            message.Status="sent";row.Status="sent";
        }
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or ArgumentException)
        {
            message.Status=ex is ArgumentException?"failed":"uncertain";row.Status=message.Status;row.Reason="Entrega sin confirmar. Revisa WhatsApp antes de volver a enviar.";
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
                var ids=await db.Tenants.Where(t=>t.RemindersEnabled).Select(t=>t.Id).ToListAsync(ct);
                foreach(var tenant in ids)
                {
                    using var s=scopes.CreateScope();s.ServiceProvider.GetRequiredService<TenantScope>().Id=tenant;
                    var scoped=s.ServiceProvider.GetRequiredService<CrmDb>();var service=s.ServiceProvider.GetRequiredService<AppointmentReminderService>();
                    try
                    {
                        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromMinutes(2));
                        await service.Sync(DateTimeOffset.UtcNow,deadline.Token);
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
            catch(Exception ex) when(!ct.IsCancellationRequested){log.LogWarning("Reminder worker unavailable {ErrorType}",ex.GetType().Name);}
            await Task.Delay(TimeSpan.FromMinutes(1),ct);
        }
    }
}
