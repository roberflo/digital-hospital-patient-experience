using System.Collections.Concurrent;
using System.Net;

namespace Recepcion.Integrations;

public sealed partial class HospitalClient
{
    /// <summary>Registers an adult through Hospital's own <c>POST v1/patients</c>, so its
    /// validation, duplicate detection and audit apply. Never forces past a duplicate: a
    /// candidate comes back as <c>Created = false</c> and a person decides.</summary>
    public async Task<HospitalRegistration> RegisterPatientAsync(Guid tenantId, HospitalPatientRegistration input, string idempotencyKey, CancellationToken ct = default)
    {
        IdempotencyKey(idempotencyKey);
        // The wa_id is already E.164 without its «+» (INV-5): Hospital stores it canonical and never guesses a country for it (criterio 16).
        var phone = "+" + (NormalizePhone(input.Phone) ?? throw new HospitalIntegrationException("hospital.patient_phone_mismatch", HttpStatusCode.BadRequest));
        // Keyed (MR-41): sent again with the same key, Hospital answers the original registration instead of a second record. Nothing here retries it.
        using var response = await SendAsync(tenantId, HttpMethod.Post, "v1/patients", new
        {
            input.GivenNames, input.FamilyNames, birthDate = input.BirthDate.ToString("yyyy-MM-dd"), input.Sex,
            emergencyContacts = new[] { new { fullName = input.EmergencyName, relationship = input.EmergencyRelationship, phone = input.EmergencyPhone } },
            forceCreateDespiteDuplicate = false, phone
        }, ct, idempotencyKey);
        return await ParseAsync<HospitalRegistration>(response, ct);
    }
    /// <summary>Names the caller for Hospital's audit trail, which stores the request's User-Agent as <c>origin_agent</c>.
    /// Without it an appointment made by the WhatsApp agent cannot be told from one a person made in the CRM.
    /// The correlation id (the agent's job) goes on every request; without one each request gets its own.</summary>
    public void CallAs(string origin, Guid? correlationId = null) { http.DefaultRequestHeaders.Remove("User-Agent"); http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", origin); correlation = correlationId; }
    Guid? correlation;

    /// <summary>How many active appointments hold a slot, by the agenda's own count; null when the agenda no longer publishes it.</summary>
    public async Task<int?> SlotOccupancyAsync(Guid tenantId, Guid doctor, DateTimeOffset start, string timeZone, CancellationToken ct = default)
    {
        var day = ClinicalDay(start, timeZone);
        var options = await GetAvailabilityAsync(tenantId, day, day, doctor, ct);
        return options.Professionals.Where(p => p.ClinicianId == doctor).SelectMany(p => p.Days).SelectMany(d => d.Slots).Where(slot => slot.StartsAt == start).Select(slot => (int?)slot.TakenBy).FirstOrDefault();
    }

    /// <summary>The address and phone the hospital published with its public booking link. Null when it published none (404), when this
    /// Hospital does not have the route yet, or when it cannot be reached or read: the agent then answers from the guide, as before.</summary>
    public async Task<HospitalClinicContact?> ClinicContact(Guid tenantId, CancellationToken ct = default)
    {
        try { return await ReadAsync<HospitalClinicContact>(tenantId, "v1/agenda/clinic-contact", ct); }
        // Everything but the caller giving up, as below: an answer that cannot be read (a proxy's page) is not worth the turn either.
        catch (Exception ex) when (!ct.IsCancellationRequested) { Degraded("clinic-contact", tenantId, ex); return null; }
    }
    /// <summary>General number (S3): the recipients of a hospital without Recepción, from criterio 41 only. Never the day agenda or the patient record (B7).</summary>
    public async Task<IReadOnlyList<HospitalReminderRecipient>> GetReminderRecipientsAsync(Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (to < from || to.DayNumber - from.DayNumber > 6) throw new ArgumentException("Consulta hasta 7 días de destinatarios");
        return (await ReadAsync<HospitalReminderRecipients>(tenantId, $"v1/agenda/whatsapp-reminders?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", ct)).Rows ?? [];
    }
    /// <summary>General number (criterio 20): L1, strictly. 404 is «nothing published» (null); any other failure is thrown, so a Hospital that is down
    /// is retried instead of being reported as a missing name.</summary>
    public async Task<HospitalClinicContact?> GetOutboundContactAsync(Guid tenantId, CancellationToken ct = default)
    {
        try { return await ReadAsync<HospitalClinicContact>(tenantId, "v1/agenda/clinic-contact", ct); }
        catch (HospitalIntegrationException ex) when (ex.Code == "hospital.http_404") { return null; }
    }
    /// <summary>Criterio 25b: a BAJA on the general number withdraws the patient's WhatsApp authorisation in Hospital, origin «whatsapp-baja».
    /// <paramref name="optedOutAt"/> is when the BAJA arrived: Hospital never withdraws an authorisation recorded after it (criterio 33).</summary>
    public async Task WithdrawWhatsAppConsentAsync(Guid tenantId, Guid patientId, DateTimeOffset optedOutAt, CancellationToken ct = default)
    {
        using var response = await SendAsync(tenantId, HttpMethod.Post, $"v1/patients/{patientId:D}/whatsapp-reminders/consent/withdraw", new { origin = "whatsapp-baja", optedOutAt }, ct);
    }
    /// <summary>Criterios 22 y 27: the patient's own cancellation from the general number. No read of the record first: criterio 41 was just re-read.</summary>
    public async Task CancelReminderAppointmentAsync(Guid tenantId, Guid appointmentId, CancellationToken ct = default)
    {
        var cancellation = AppointmentCancellation.From("patient-requested");
        using var response = await SendAsync(tenantId, HttpMethod.Post, $"v1/agenda/{appointmentId:D}/cancel", new { reason = cancellation.Reason, cancelledByPatient = cancellation.CancelledByPatient }, ct);
    }
    /// <summary>These two reads fail in silence for the patient, so a failure that stays would be seen by nobody: it is logged. A 404 is an
    /// answer (nothing published, or a Hospital without the route), not a failure. Only the error's type or code: never a body, a URL or a token.</summary>
    void Degraded(string read, Guid tenantId, Exception ex)
    {
        if (ex is not HospitalIntegrationException { Code: "hospital.http_404" }) log?.LogWarning("Hospital read unavailable {Read} {TenantId} {ErrorType}", read, tenantId, (ex as HospitalIntegrationException)?.Code ?? ex.GetType().Name);
    }

    // ponytail: one dictionary in this process, never trimmed (one small entry per hospital) and with no lock: two turns arriving together on a
    // cold entry both ask, and each replica keeps its own copy. A shared cache if replicas or hospitals multiply.
    static readonly ConcurrentDictionary<Guid, (DateTimeOffset Until, IReadOnlyDictionary<Guid, string> Known)> Disciplines = new();
    static readonly IReadOnlyDictionary<Guid, string> NoDisciplines = new Dictionary<Guid, string>();
    /// <summary>The discipline of each professional on the active roll that Hospital knows it for (medicine, dentistry, nutrition).
    /// The read costs Hospital one identity-provider call per staff account, so the answer is kept an hour per hospital. A failure of any
    /// kind (the route not deployed yet, its identity provider down, an answer that cannot be read) is an empty answer kept five minutes:
    /// the agenda never fails for it, a struggling Hospital is not asked on every turn, and a later deploy starts working without a restart.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> ClinicianDisciplines(Guid tenantId, CancellationToken ct = default)
    {
        if (Disciplines.TryGetValue(tenantId, out var kept) && kept.Until > DateTimeOffset.UtcNow) return kept.Known;
        var known = NoDisciplines; var keep = TimeSpan.FromMinutes(5);
        try
        {
            var roll = await ReadAsync<HospitalClinicianDisciplines>(tenantId, "v1/clinic/clinician-disciplines", ct);
            known = (roll.Clinicians ?? []).Where(c => c.Discipline is { Length: > 0 }).GroupBy(c => c.ClinicianId).ToDictionary(g => g.Key, g => g.First().Discipline!);
            keep = TimeSpan.FromHours(1);
        }
        // Everything but the caller giving up: this only adds a label to hours the agent already has.
        catch (Exception ex) when (!ct.IsCancellationRequested) { Degraded("clinician-disciplines", tenantId, ex); }
        Disciplines[tenantId] = (DateTimeOffset.UtcNow + keep, known);
        return known;
    }
}
/// <summary>Hospital also answers how long it takes to confirm an online request; that is about its web form, not something the WhatsApp agent says, so it is not read.</summary>
/// <remarks><see cref="OutboundDisplayName"/>: the name a person approved for messages to patients (the live booking link's public name,
/// enmienda E-RL1); the general number's reminder names the clinic with it and nothing else (criterio 20).</remarks>
public sealed record HospitalClinicContact(string? PublicAddress, string? PublicPhone, string? OutboundDisplayName = null);
/// <summary>Criterio 41's minimal contract: one booked appointment whose patient has a current WhatsApp authorisation tied to this phone, and
/// since when that authorisation holds (A20: an authorisation recorded after a BAJA on the general number counts again, for that hospital). Nothing else.</summary>
public sealed record HospitalReminderRecipient(Guid AppointmentId, Guid PatientId, DateTimeOffset ScheduledStart, string Phone, DateTimeOffset? ConsentSince = null)
{
    public override string ToString() => $"HospitalReminderRecipient {AppointmentId}";
}
public sealed record HospitalReminderRecipients(IReadOnlyList<HospitalReminderRecipient> Rows);
public sealed record HospitalClinicianDiscipline(Guid ClinicianId, string? Discipline);
public sealed record HospitalClinicianDisciplines(IReadOnlyList<HospitalClinicianDiscipline>? Clinicians);
public sealed record HospitalPatientRegistration(string GivenNames, string FamilyNames, DateOnly BirthDate, string Sex, string Phone,
    string EmergencyName, string EmergencyRelationship, string EmergencyPhone)
{
    public override string ToString() => "HospitalPatientRegistration";
}
public sealed record HospitalRegistration(bool Created, Guid? PatientId);
