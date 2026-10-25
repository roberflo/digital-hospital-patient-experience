using System.Net;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

/// <summary>recordatorios-whatsapp.spec.md S3-R: the reminders of a hospital without Recepción (<c>Tenant.Mode == "reminders"</c>), sent from the
/// platform's general number. No contact, conversation or message exists for them: a row holds the patient, the appointment and the recipient's
/// HMAC, and the phone is read again from Hospital's minimal contract (criterio 41) right before each send.</summary>
public sealed partial class AppointmentReminderService
{
    /// <summary>The note on a row whose withdrawal Hospital refused for good (4xx): no more retries; the owner's doubts list shows it.</summary>
    public const string WithdrawalRefused = "Hospital rechazó registrar el retiro de la autorización tras BAJA; revíselo con la clínica.";
    public const string UnusablePhone = "Teléfono inválido o sin código de país; el número general no puede escribirle.";
    /// <summary>Why a valid number of another country is not written to (criterio 23): in branch (A), only after a send to it came back without
    /// its wa_id. It reopens like the others.</summary>
    public const string OutsideList = "País fuera de la lista del número general (+503, +502, +504, +1).";
    /// <summary>Criterio 23, the safety net of branch (A): a send outside the closed list whose wa_id did not come back.</summary>
    public const string WaIdMissing = "No se pudo confirmar el número de WhatsApp; los siguientes recordatorios a este paciente se detienen.";
    /// <summary>The text rows were skipped with while only +503 was written to; they reopen like the current one (criterio 18).</summary>
    const string PreviousUnusablePhone = "Teléfono inválido o fuera de El Salvador (+503); el número general aún no lo admite.";

    /// <summary>The closed list of criterios 23 and 31: null for +503, +502, +504 and +1, the countries whose WhatsApp wa_id is taken to be their
    /// E.164; <see cref="OutsideList"/> for another country written with «+»; <see cref="UnusablePhone"/> otherwise. Without «+», only 11 digits
    /// starting with 503, as a Hospital deployed before E.164 sends them; the country of anything else is not guessed.</summary>
    static string? Closed(string? phone)
    {
        string digits;
        try { digits = Rules.Phone(phone ?? ""); } catch (ArgumentException) { return UnusablePhone; }
        var listed = digits.StartsWith("503", StringComparison.Ordinal) || phone!.TrimStart().StartsWith('+') && (digits.StartsWith("502", StringComparison.Ordinal) || digits.StartsWith("504", StringComparison.Ordinal) || digits.StartsWith('1'));
        return listed && digits.Length == 11 ? null : listed || !phone!.TrimStart().StartsWith('+') ? UnusablePhone : OutsideList;
    }
    /// <summary>Why the general number does not write to <paramref name="phone"/>, or null when it does. Branch (A) of criterio 21 (measured
    /// 2026-10-25: Kapso returns <c>contacts[].wa_id</c>): any country with its «+», because each send stores the hash of its wa_id and a BAJA
    /// or CANCELAR from it matches (criterio 22, INV-8). An E.164 of 8 digits in all stays unusable (criterio 30): its wa_id could never match.
    /// Outside the closed list, a send without its wa_id stops the next ones (<see cref="Unconfirmed"/>).</summary>
    public static string? Refusal(string? phone) => Closed(phone) is OutsideList ? Rules.Phone(phone!).Length == 8 ? UnusablePhone : null : Closed(phone);
    /// <summary>The HMAC of a recipient the general number may write to (<see cref="Refusal"/> is null), or null.</summary>
    public static string? GeneralRecipientHash(string? phone, string key) => Refusal(phone) is null ? Rules.PhoneHash(phone!, key) : null;
    string? Hash(string? phone) => GeneralRecipientHash(phone, config["PHONE_HASH_KEY"]!);

    /// <summary>Criterio 19, A20: a phone on the general number's opt-out list gets nothing in this hospital, unless Hospital says this hospital
    /// recorded the patient's authorisation after that BAJA (<c>consentSince</c>): the desk decided with the patient there, for this hospital only.
    /// A BAJA from the wa_id a send to that phone came back with counts too (TEL-1 criterio 22c), whichever hospital sent it: the wa_id is the phone's.</summary>
    async Task<Func<string, DateTimeOffset?, bool>> OptedOut(IEnumerable<string?> hashes, CancellationToken ct)
    {
        var list = hashes.OfType<string>().Distinct().ToArray();
        var waIds = (await db.AppointmentReminders.IgnoreQueryFilters().AsNoTracking().Where(r => r.WaIdHash != null && list.Contains(r.RecipientHash!))
            .Select(r => new { Recipient = r.RecipientHash!, WaId = r.WaIdHash! }).Distinct().ToListAsync(ct)).ToLookup(x => x.Recipient, x => x.WaId);
        var all = list.Concat(waIds.SelectMany(g => g)).Distinct().ToArray();
        var optOuts = await db.GeneralOptOuts.AsNoTracking().Where(o => all.Contains(o.PhoneHash)).ToDictionaryAsync(o => o.PhoneHash, o => o.CreatedAt, ct);
        return (hash, consentSince) => waIds[hash].Prepend(hash).Any(h => optOuts.TryGetValue(h, out var since) && !(consentSince > since));
    }

    /// <summary>Criterio 23, safety net of branch (A): the recipients among <paramref name="hashes"/> that a send may have reached without its wa_id
    /// coming back. Read across hospitals, like the BAJA (review Important 1): the wa_id is the phone's, so a BAJA from it could not stop this
    /// hospital either. Only hashes, never a phone.</summary>
    async Task<HashSet<string>> Unconfirmed(IEnumerable<string?> hashes, CancellationToken ct)
    {
        var list = hashes.OfType<string>().Distinct().ToArray();
        return list.Length == 0 ? [] : (await db.AppointmentReminders.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.RecipientHash != null && list.Contains(r.RecipientHash) && r.AttemptedAt != null && r.WaIdHash == null && r.ContactId == null && r.Status != "failed" && r.Status != "sending")
            .Select(r => r.RecipientHash!).Distinct().ToListAsync(ct)).ToHashSet();
    }

    async Task SyncGeneral(Tenant tenant, DateTimeOffset now, CancellationToken ct)
    {
        // ponytail: a general-number clinic is read every 15 minutes, not every minute: each read is one audited Hospital read of a set of patients.
        // Lower it if a booking made in the last minutes before 09:00 must still get its day-before reminder.
        if (!tenant.RemindersEnabled || tenant.ReminderLastSyncAt > now.AddMinutes(-15)) return;
        var day = HospitalClient.ClinicalDay(now, tenant.TimeZone);
        var current = await hospital.GetReminderRecipientsAsync(scope.Id, day, day.AddDays(2), ct);
        // One unusable phone never stops the others: its rows say why (criterio 19).
        var stopped = await Unconfirmed(current.Where(r => Closed(r.Phone) is OutsideList).Select(r => Hash(r.Phone)), ct);
        var recipients = current.Select(r => Hash(r.Phone) is { } hash && stopped.Contains(hash)
            ? (Id: r.AppointmentId, Start: ReminderRules.Instant(r.ScheduledStart), r.PatientId, Hash: (string?)null, Refusal: (string?)OutsideList, r.ConsentSince)
            : (Id: r.AppointmentId, Start: ReminderRules.Instant(r.ScheduledStart), r.PatientId, Hash: Hash(r.Phone), Refusal: Refusal(r.Phone), r.ConsentSince)).ToList();
        var pending = await db.AppointmentReminders.Where(r => r.Status == "pending" || r.Status == "sending").ToListAsync(ct);
        foreach (var row in pending.Where(r => r.Status == "pending" && r.RecipientHash is { } h && stopped.Contains(h))) { row.Status = "skipped"; row.Reason = OutsideList; }
        foreach (var row in pending.Where(r => r.Status == "sending" && r.AttemptedAt < now.AddMinutes(-10))) { row.Status = "uncertain"; row.Reason = "Envío interrumpido; no se repite."; }
        var optedOut = await OptedOut(recipients.Select(r => r.Hash).Concat(pending.Select(r => r.RecipientHash)), ct);
        foreach (var row in pending.Where(r => r.Status == "pending"))
        {
            var match = recipients.FirstOrDefault(r => r.Id == row.AppointmentId && r.Start == row.StartsAt && r.PatientId == row.PatientId && r.Hash != null && r.Hash == row.RecipientHash);
            if (match.Hash is null || optedOut(match.Hash, match.ConsentSince))
            { row.Status = "cancelled"; row.Reason = "La cita, el teléfono o la autorización cambiaron en Hospital, o el teléfono pidió BAJA."; }
        }
        var unusable = 0;
        foreach (var r in recipients.Where(r => r.Start > now && (r.Hash is null || !optedOut(r.Hash, r.ConsentSince))))
        {
            if (r.Hash is null) unusable++;
            foreach (var window in ReminderRules.Windows)
            {
                var existing = await db.AppointmentReminders.SingleOrDefaultAsync(x => x.AppointmentId == r.Id && x.StartsAt == r.Start && x.Window == window, ct);
                if (existing is not null)
                {
                    var reopen = existing.AttemptedAt is null && existing.ContactId is null && existing.PatientId == r.PatientId && r.Hash is not null
                        && (existing.Status == "cancelled" || existing.Status == "skipped" && existing.Reason is UnusablePhone or PreviousUnusablePhone or OutsideList);
                    if (reopen) { existing.RecipientHash = r.Hash; existing.Status = "pending"; existing.Reason = null; }
                    // Still unwritable: the row says why today (a text from before criterio 23 is replaced by the current one).
                    else if (r.Hash is null && existing.Status == "skipped" && existing.Reason is UnusablePhone or PreviousUnusablePhone or OutsideList) existing.Reason = r.Refusal;
                    continue;
                }
                var due = ReminderRules.Due(r.Start, window, tenant.TimeZone);
                var late = due < now.AddMinutes(-15);
                db.AppointmentReminders.Add(new() { TenantId = scope.Id, PatientId = r.PatientId, RecipientHash = r.Hash, AppointmentId = r.Id, StartsAt = r.Start, DueAt = due, Window = window, CreatedAt = now,
                    Status = late || r.Hash is null ? "skipped" : "pending", Reason = late ? "El horario del recordatorio ya pasó." : r.Hash is null ? r.Refusal : null });
            }
        }
        tenant.ReminderLastSyncAt = now;
        tenant.ReminderSyncError = unusable > 0 ? $"{unusable} cita(s) con un teléfono que el número general no admite; no se les envía." : null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The send of one general-number row, under the tenant's reminders lock. Platform credentials first (criterio 40: nothing is called
    /// without them), then the opt-out list, then L1's approved name and phone (criterio 20), then criterio 41 again (INV-1): a hospital that
    /// has published no name is never asked for its patients.</summary>
    async Task DispatchGeneral(AppointmentReminder row, Tenant tenant, DateTimeOffset now, CancellationToken ct)
    {
        async Task Wait(string reason) { row.Reason = reason; await db.SaveChangesAsync(ct); }
        async Task Stop(string reason) { row.Status = "cancelled"; row.Reason = reason; await db.SaveChangesAsync(ct); }
        string number;
        try { number = kapso.GeneralNumber(); kapso.GeneralKey(); }
        catch (ArgumentException ex) { await Wait(ex.Message); return; }
        if (row.RecipientHash is null) { await Stop(UnusablePhone); return; }
        var optedOut = await OptedOut([row.RecipientHash], ct); // decided below, with the consentSince Hospital gives now
        var clinic = await hospital.GetOutboundContactAsync(scope.Id, ct);
        if (clinic?.OutboundDisplayName?.Trim() is not { Length: > 0 } name) { await Wait("Falta el nombre público de la clínica."); return; }
        if (clinic.PublicPhone?.Trim() is not { Length: > 0 } clinicPhone) { await Wait("Falta el teléfono público de la clínica."); return; }
        var day = HospitalClient.ClinicalDay(row.StartsAt, tenant.TimeZone);
        var recipient = (await hospital.GetReminderRecipientsAsync(scope.Id, day, day, ct)).FirstOrDefault(r =>
            r.AppointmentId == row.AppointmentId && ReminderRules.Instant(r.ScheduledStart) == row.StartsAt && r.PatientId == row.PatientId && Hash(r.Phone) == row.RecipientHash);
        if (recipient is null) { await Stop("Cita cancelada o reprogramada, teléfono cambiado o autorización retirada en Hospital."); return; }
        if (optedOut(row.RecipientHash, recipient.ConsentSince)) { await Stop("El teléfono pidió BAJA en el número general."); return; }
        var outside = Closed(recipient.Phone) is OutsideList;
        if (outside && (await Unconfirmed([row.RecipientHash], ct)).Count > 0) { row.Status = "skipped"; row.Reason = OutsideList; await db.SaveChangesAsync(ct); return; }
        var dayBefore = row.Window == "day_before";
        var template = dayBefore ? KapsoClient.GeneralDayTemplate : KapsoClient.GeneralHourTemplate;
        if (!await kapso.ReminderTemplateApproved(number, template, tenant.ReminderLanguage, dayBefore ? KapsoClient.GeneralDayWindow : KapsoClient.GeneralHourWindow, ct, general: true)) { await Wait("Plantilla pendiente de aprobación o incompatible."); return; }
        row.Status = "sending"; row.AttemptedAt = now; row.Reason = null;
        await db.SaveChangesAsync(ct); // Durable attempt before external I/O, never automatically replayed.
        var (date, time) = ReminderRules.Local(row.StartsAt, tenant.TimeZone);
        try
        {
            var (_, waId) = await kapso.SendReminder(number, recipient.Phone, template, tenant.ReminderLanguage, name, date, time, ct, dayBefore ? [$"CANCELAR {row.Id}"] : null, general: true, phoneOfClinic: clinicPhone);
            row.Status = "sent";
            // Criterio 22: only the wa_id's HMAC, never the wa_id. One of 8 digits is unusable (criterio 30), like one that did not come back.
            try { row.WaIdHash = waId is null ? null : Rules.PhoneHash(Rules.WaId(waId), config["PHONE_HASH_KEY"]!); } catch (ArgumentException) { row.WaIdHash = null; }
            if (row.WaIdHash is null && outside) db.Activities.Add(new Activity { TenantId = scope.Id, Actor = "Recordatorios", ActorRole = "system", Kind = "general_wa_id_missing", Body = WaIdMissing });
            // A BAJA from this wa_id that came before any send returned it (review Important 1): now it is known to be this patient's, so the
            // withdrawal is asked of Hospital, at the BAJA's moment, by the worker's retry (criterio 25b); OptedOut stops the next sends.
            // After the external send: never the worker's token, or stopping it would turn a delivered reminder into a doubt (like the save below).
            if (row.WaIdHash is { } wa && await db.GeneralOptOuts.AsNoTracking().Where(o => o.PhoneHash == wa).Select(o => (DateTimeOffset?)o.CreatedAt).SingleOrDefaultAsync(CancellationToken.None) is { } baja
                && !(recipient.ConsentSince > baja)) row.OptOutPendingAt = baja;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException)
        {
            var refused = ex is HttpRequestException { StatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError };
            row.Status = refused || ex is ArgumentException ? "failed" : "uncertain";
            row.Reason = refused ? "WhatsApp rechazó el envío; no se entregó." : "Entrega sin confirmar; no se repite.";
        }
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Criterio 25b: records in Hospital, once per patient, the withdrawal a BAJA asked for on the rows the general number had already
    /// sent in this hospital, whatever its mode now (a hospital that moved to Recepción included). A failure leaves it pending for the next
    /// pass; the opt-out list already stops every send meanwhile.</summary>
    public async Task RetryOptOuts(CancellationToken ct = default)
    {
        var rows = await db.AppointmentReminders.Where(r => r.OptOutPendingAt != null).ToListAsync(ct);
        foreach (var patient in rows.GroupBy(r => r.PatientId))
        {
            // The last BAJA. Hospital keeps an authorisation recorded after it (criterio 33): nothing is read here to decide that.
            // Clocks: optedOutAt is Recepción's clock and the authorisation's instant Hospital's; a skew of seconds only matters if the desk
            // records the authorisation within those seconds of the BAJA.
            var optedOutAt = patient.Max(r => r.OptOutPendingAt!.Value);
            try
            {
                await hospital.WithdrawWhatsAppConsentAsync(scope.Id, patient.Key, optedOutAt, ct);
                foreach (var row in patient) { row.OptOutPendingAt = null; row.OptOutWithdrawnAt = DateTimeOffset.UtcNow; }
            }
            // Refused for good (4xx but 408/429): the attempt ends, with a note the owner's doubts list shows. Nothing else is retried here.
            catch (HospitalIntegrationException ex) when ((int)ex.StatusCode is >= 400 and < 500 and not 408 and not 429)
            {
                // Added to what the row said: a cancellation in doubt keeps its «Hospital no confirmó…».
                foreach (var row in patient) { row.OptOutPendingAt = null; row.Reason = row.Reason is { Length: > 0 } said && !said.Contains(WithdrawalRefused, StringComparison.Ordinal) ? said + " " + WithdrawalRefused : WithdrawalRefused; }
            }
            catch (Exception ex) when (ex is HospitalIntegrationException or HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested) { continue; }
            await db.SaveChangesAsync(ct);
        }
    }
}
