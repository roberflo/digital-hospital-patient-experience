using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

/// <summary>recordatorios-whatsapp.spec.md S3-R (criterios 22–26, 39; INV-7–INV-9; A17): what patients write to the platform's general number.
/// No agent, no contact, no conversation, no message: a tap is routed only by the reminder id it carries, BAJA goes to the opt-out list,
/// and anything else gets one fixed line a day. Signed with the platform's own secret, never a clinic's.</summary>
public static partial class GeneralNumber
{
    public const string FreeText = "Este número sólo envía recordatorios. Para hablar con su clínica, llame al número que aparece en su recordatorio. Para no recibir más recordatorios escriba BAJA.";
    const string OptedOut = "Listo. Ya no recibirá recordatorios de citas desde este número.";
    const string Kept = "De acuerdo, su cita se mantiene.";
    const string Cancelled = "Su cita fue cancelada.";
    const string NotCancelled = "No pudimos confirmar la cancelación. Por favor llame a su clínica.";

    /// <summary>At startup: the platform's account is never a clinic's (criterio 39). The same key, secret or number in both stops the start.</summary>
    public static void RequireSeparateCredentials(IConfiguration config)
    {
        // The number is compared against the clinics' real channels instead (RequireNotAClinicNumber): KAPSO_PHONE_NUMBER_ID is only the demo's.
        foreach (var (platform, clinic) in new[] { ("KAPSO_PLATFORM_API_KEY", "KAPSO_API_KEY"), ("KAPSO_PLATFORM_WEBHOOK_SECRET", "KAPSO_WEBHOOK_SECRET") })
            if (config[platform] is { Length: > 0 } value && value == config[clinic]) throw new InvalidOperationException($"{platform} must differ from {clinic}: the general number never uses a clinic's Kapso account");
    }

    /// <summary>Every way a clinic's channel is created calls this: the platform's general number is never one (criterio 39).</summary>
    public static void RequireNotPlatformNumber(string phoneNumberId, IConfiguration config)
    {
        if (config["KAPSO_PLATFORM_PHONE_NUMBER_ID"] is { Length: > 0 } number && phoneNumberId == number) throw new ArgumentException("Ese es el número general de la plataforma; no puede ser canal de una clínica.");
    }
    /// <summary>At startup, after migrations: the general number is no clinic's channel (a clinic's webhook would claim its messages).</summary>
    public static async Task RequireNotAClinicNumber(CrmDb db, IConfiguration config)
    {
        if (config["KAPSO_PLATFORM_PHONE_NUMBER_ID"] is { Length: > 0 } number && await db.Channels.IgnoreQueryFilters().AnyAsync(c => c.PhoneNumberId == number))
            throw new InvalidOperationException("KAPSO_PLATFORM_PHONE_NUMBER_ID is a clinic's channel: the general number must be the platform's own");
    }

    // ponytail: redeliveries are recognised by message id in this process only, for ten minutes; each replica keeps its own. Every act below is
    // safe to repeat anyway (the cancellation is atomic, BAJA and the free-text line are capped): this only spares a second «¿Confirma…?».
    static readonly ConcurrentDictionary<string, DateTimeOffset> Seen = new();
    static string Key(string messageId) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(messageId)));
    static void Forget(string? messageId) { if (!string.IsNullOrEmpty(messageId)) Seen.TryRemove(Key(messageId), out _); }
    static bool FirstDelivery(string? messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return true;
        var now = DateTimeOffset.UtcNow;
        foreach (var old in Seen.Where(x => x.Value < now.AddMinutes(-10))) Seen.TryRemove(old);
        return Seen.TryAdd(Key(messageId), now);
    }

    public static async Task<IResult> Receive(HttpContext ctx, CrmDb db, TenantScope scope, IConfiguration config, KapsoClient kapso, HospitalClient hospital, ILoggerFactory logs)
    {
        if (await WhatsAppEndpoints.Body(ctx) is not { } body) return Results.StatusCode(413);
        if (!Rules.VerifySignature(body, ctx.Request.Headers["X-Webhook-Signature"].ToString(), config["KAPSO_PLATFORM_WEBHOOK_SECRET"] ?? "")) return Results.Unauthorized();
        JsonElement[] items;
        try
        {
            using var json = JsonDocument.Parse(body); var root = json.RootElement;
            items = (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("batch", out var batch) && batch.ValueKind == JsonValueKind.True ? root.GetProperty("data").EnumerateArray().ToArray() : [root]).Select(x => x.Clone()).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { return Results.BadRequest(); }
        if (items.Any(x => x.ValueKind != JsonValueKind.Object)) return Results.BadRequest();
        var log = logs.CreateLogger("Recepcion.GeneralNumber");
        if (ctx.Request.Headers["X-Webhook-Event"].ToString() != "whatsapp.message.received") return Results.Ok(new { accepted = true });
        foreach (var item in items)
        {
            if (WhatsAppEndpoints.Get(item, "phone_number_id") is not { } number || number != config["KAPSO_PLATFORM_PHONE_NUMBER_ID"]) continue;
            if (!item.TryGetProperty("message", out var msg)) continue;
            var k = msg.TryGetProperty("kapso", out var ka) ? ka : default;
            var messageId = WhatsAppEndpoints.Get(msg, "id");
            if (WhatsAppEndpoints.Get(k, "direction") == "outbound" || WhatsAppEndpoints.Get(k, "origin") == "history_sync" || !FirstDelivery(messageId)) continue;
            try
            {
                var from = (item.TryGetProperty("conversation", out var conversation) ? WhatsAppEndpoints.Get(conversation, "phone_number") : null) ?? WhatsAppEndpoints.Get(msg, "from");
                string hash;
                try { hash = Rules.PhoneHash(Rules.WaId(from ?? ""), config["PHONE_HASH_KEY"]!); } catch (ArgumentException) { continue; } // criterio 30
                // Never the phone, the body or an id in the log (INV-8).
                async Task Reply(string? text, object? interactive = null)
                {
                    try { await kapso.SendGeneral(from!, text, interactive, ctx.RequestAborted); }
                    catch (Exception ex) when (ex is ArgumentException or HttpRequestException or TaskCanceledException) { log.LogWarning("General number reply not sent {ErrorType}", ex.GetType().Name); }
                }
                var said = Said(msg, k).Trim();
                var tap = Tap().Match(said);
                if (tap.Success && Guid.TryParse(tap.Groups[2].Value, out var id))
                {
                    var row = await Resolve(id, hash, db, scope, config, hospital, ctx.RequestAborted);
                    if (row is not null)
                    {
                        switch (tap.Groups[1].Value)
                        {
                            case "CANCELAR":
                                var (date, time) = ReminderRules.Local(row.StartsAt, await db.Tenants.Where(t => t.Id == row.TenantId).Select(t => t.TimeZone).SingleAsync(ctx.RequestAborted));
                                await Reply(null, new Choices([new($"CANCELAR-SI {row.Id}", "Sí, cancelar"), new($"CANCELAR-NO {row.Id}", "No")]).ToWhatsApp($"¿Confirma que desea cancelar su cita del {date} a las {time}?"));
                                continue;
                            case "CANCELAR-NO": await Reply(Kept); continue;
                            default: await Reply(await Cancel(row, db, hospital, log, ctx.RequestAborted) ? Cancelled : NotCancelled); continue;
                        }
                    }
                }
                else if (ReminderRules.ConsentCommand(said) == "off")
                {
                    await OptOut(hash, db, scope, config, kapso, hospital, ctx.RequestAborted);
                    // Its own daily cap: the first BAJA of the day is always answered, even after the free-text line (N1); a repeated one is not.
                    if (await Capped(hash + ":baja", db, ctx.RequestAborted)) await Reply(OptedOut);
                    continue;
                }
                // Free text, and every tap that does not hold: the same line, under the same cap (criterios 23, 24; INV-8).
                if (await Capped(hash, db, ctx.RequestAborted)) await Reply(FreeText);
            }
            catch { Forget(messageId); throw; } // a message that failed is not «seen»: Kapso's redelivery is processed (N3)
        }
        return Results.Ok(new { accepted = true });
    }

    /// <summary>What the patient sent: a template quick reply's payload, an interactive reply's id, or the text.</summary>
    static string Said(JsonElement msg, JsonElement kapso)
    {
        if (msg.TryGetProperty("button", out var button) && WhatsAppEndpoints.Get(button, "payload") is { } payload) return payload;
        if (msg.TryGetProperty("interactive", out var interactive) && interactive.ValueKind == JsonValueKind.Object && interactive.TryGetProperty("button_reply", out var reply) && WhatsAppEndpoints.Get(reply, "id") is { } choice) return choice;
        return (msg.TryGetProperty("text", out var text) ? WhatsAppEndpoints.Get(text, "body") : null) ?? WhatsAppEndpoints.Get(kapso, "content") ?? "";
    }
    [GeneratedRegex(@"^(CANCELAR|CANCELAR-SI|CANCELAR-NO) ([0-9a-fA-F-]{36})$")] private static partial Regex Tap();

    /// <summary>Criterio 22 (a)–(c), INV-6, INV-7: the row the payload names, a day-before reminder of a general-number hospital that was sent, still
    /// ahead, to this very phone (its recipient's hash, or the hash of the wa_id the send came back with: TEL-1 criterio 22a); and Hospital still
    /// lists it (criterio 41) at that start for that patient and the row's recipient, never the sender's hash.
    /// Anything else is null and reads like free text. Sets the scope to the row's hospital.</summary>
    static async Task<AppointmentReminder?> Resolve(Guid id, string hash, CrmDb db, TenantScope scope, IConfiguration config, HospitalClient hospital, CancellationToken ct)
    {
        var row = await db.AppointmentReminders.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (row is null || row.Window != "day_before" || row.Status != "sent" || row.RecipientHash != hash && row.WaIdHash != hash || row.StartsAt <= DateTimeOffset.UtcNow) return null;
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == row.TenantId && t.Mode == "reminders", ct);
        if (tenant is null) return null;
        scope.Id = row.TenantId;
        var day = HospitalClient.ClinicalDay(row.StartsAt, tenant.TimeZone);
        try
        {
            return (await hospital.GetReminderRecipientsAsync(row.TenantId, day, day, ct)).Any(r => r.AppointmentId == row.AppointmentId && r.PatientId == row.PatientId
                && ReminderRules.Instant(r.ScheduledStart) == row.StartsAt && AppointmentReminderService.GeneralRecipientHash(r.Phone, config["PHONE_HASH_KEY"]!) == row.RecipientHash) ? row : null;
        }
        catch (Exception ex) when (ex is HospitalIntegrationException or HttpRequestException or ArgumentException or JsonException || ex is TaskCanceledException && !ct.IsCancellationRequested) { return null; }
    }

    /// <summary>«Sí, cancelar»: sent ⇒ cancelling, atomically, so a double tap or a redelivery reaches Hospital once; then Hospital cancels it as the
    /// patient's request (criterio 27). A refusal puts the row back; a timeout leaves it «cancelling» for a person, never retried here.</summary>
    static async Task<bool> Cancel(AppointmentReminder row, CrmDb db, HospitalClient hospital, ILogger log, CancellationToken ct)
    {
        if (await db.AppointmentReminders.Where(r => r.Id == row.Id && r.Status == "sent").ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "cancelling"), ct) != 1) return false;
        string status, note;
        try
        {
            await hospital.CancelReminderAppointmentAsync(row.TenantId, row.AppointmentId, ct);
            (status, note) = ("cancelled", "El paciente canceló su cita desde el recordatorio del número general.");
        }
        catch (HospitalIntegrationException ex) when ((int)ex.StatusCode is >= 400 and < 500) { (status, note) = ("sent", "El paciente pidió cancelar desde el recordatorio del número general; Hospital no lo aceptó."); }
        catch (Exception ex) when (ex is HospitalIntegrationException or HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        { log.LogWarning("General number cancellation unconfirmed {TenantId} {ErrorType}", row.TenantId, ex.GetType().Name); (status, note) = ("cancelling", "El paciente pidió cancelar desde el recordatorio del número general; Hospital no confirmó. Verifique la agenda."); }
        await db.AppointmentReminders.Where(r => r.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status).SetProperty(r => r.Reason, status == "cancelled" ? null : note), CancellationToken.None);
        db.Activities.Add(new Activity { TenantId = row.TenantId, Actor = "Recordatorios", ActorRole = "system", Kind = "appointment_cancelled_general", Body = note });
        await db.SaveChangesAsync(CancellationToken.None);
        return status == "cancelled";
    }

    /// <summary>Criterio 24, A17: at most one fixed reply per phone every 24 h. Atomic: the row is claimed only if absent or expired. Expired rows go.</summary>
    static async Task<bool> Capped(string hash, CrmDb db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow; var expired = now.AddHours(-24);
        await db.GeneralReplyCaps.Where(c => c.LastReplyAt < expired).ExecuteDeleteAsync(ct);
        return await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"GeneralReplyCaps\" (\"PhoneHash\", \"LastReplyAt\") VALUES ({hash}, {now}) ON CONFLICT (\"PhoneHash\") DO UPDATE SET \"LastReplyAt\" = EXCLUDED.\"LastReplyAt\" WHERE \"GeneralReplyCaps\".\"LastReplyAt\" < {expired}", ct) == 1;
    }

    /// <summary>Criterio 25, A14: BAJA is for every hospital on the general number, whatever its mode now. The hash goes on the opt-out list; in each
    /// hospital whose rows carry it (read across hospitals by hash, written one hospital at a time), pending sends are cancelled, and the ones that
    /// may have gone out (sent, cancelled, cancelling, uncertain) and are not yet covered by a withdrawal ask Hospital to record it, once per patient: ten BAJA are
    /// one withdrawal. A send that failed never reached the phone. Hospital down: it stays pending for the worker; the list already stops sends.
    /// A wa_id that differs from the E.164 stops every row of the recipients whose sends came back with it (TEL-1 criterio 22b).</summary>
    static async Task OptOut(string hash, CrmDb db, TenantScope scope, IConfiguration config, KapsoClient kapso, HospitalClient hospital, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"GeneralOptOuts\" (\"PhoneHash\", \"CreatedAt\") VALUES ({hash}, {now}) ON CONFLICT (\"PhoneHash\") DO UPDATE SET \"CreatedAt\" = EXCLUDED.\"CreatedAt\"", ct);
        var recipients = (await db.AppointmentReminders.IgnoreQueryFilters().Where(r => r.WaIdHash == hash && r.RecipientHash != null).Select(r => r.RecipientHash!).Distinct().ToListAsync(ct)).Append(hash).Distinct().ToArray();
        var tenants = await db.AppointmentReminders.IgnoreQueryFilters().Where(r => recipients.Contains(r.RecipientHash!)).Select(r => r.TenantId).Distinct().ToListAsync(ct);
        foreach (var tenant in tenants)
        {
            scope.Id = tenant;
            foreach (var row in await db.AppointmentReminders.Where(r => recipients.Contains(r.RecipientHash!)).ToListAsync(ct))
            {
                if (row.Status == "pending") { row.Status = "cancelled"; row.Reason = "El teléfono pidió BAJA en el número general."; }
                else if (row.Status is "sent" or "cancelled" or "cancelling" or "uncertain" && row.AttemptedAt is { } at && (row.OptOutWithdrawnAt is null || at > row.OptOutWithdrawnAt)) row.OptOutPendingAt = now; // the last BAJA (R3)
            }
            await db.SaveChangesAsync(ct);
            await new AppointmentReminderService(db, scope, hospital, kapso, new ConversationService(db, scope, kapso), config).RetryOptOuts(ct);
            db.ChangeTracker.Clear();
        }
    }

    public static void MapGeneralNumber(this WebApplication app) => app.MapPost("/webhooks/kapso/general", Receive);
}
