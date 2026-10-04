using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Recepcion.Integrations;

/// <summary>Calls the hospital's existing v1 contracts. Configuration is keyed by the
/// authenticated CRM tenant, never by a URL or bearer token submitted by a patient.</summary>
public sealed partial class HospitalClient(HttpClient http, IConfiguration configuration, HospitalConnectionStore? connections = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsConfigured(Guid tenantId) =>
        !string.IsNullOrWhiteSpace(Tenant(tenantId)["BaseUrl"]);

    /// <summary>The hospital has no phone-search API. The caller supplies its tenant-scoped
    /// contact's linked patient id; this verifies the current hospital phone before disclosure.</summary>
    public async Task<HospitalPatient> GetVerifiedPatientAsync(
        Guid tenantId, Guid patientId, string senderPhone, CancellationToken ct = default)
    {
        var patient = await ReadAsync<HospitalPatient>(tenantId, $"v1/patients/{patientId:D}", ct);
        if (patient.PatientId != patientId || NormalizePhone(patient.Phone) != NormalizePhone(senderPhone)
            || NormalizePhone(senderPhone) is null)
            throw new HospitalIntegrationException("hospital.patient_phone_mismatch", HttpStatusCode.NotFound);
        return patient;
    }

    public Task<HospitalBookingOptions> GetAvailabilityAsync(Guid tenantId, DateOnly from,
        DateOnly to, Guid? clinicianId = null, CancellationToken ct = default)
    {
        if (to < from || to.DayNumber - from.DayNumber > 30)
            throw new ArgumentException("Availability requires a range of at most 31 days.");
        var filter = clinicianId is { } id ? $"&clinicianId={id:D}" : "";
        return ReadAsync<HospitalBookingOptions>(tenantId,
            $"v1/agenda/booking-options?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}{filter}", ct);
    }

    public Task<JsonElement> GetAgendaDayAsync(Guid tenantId, DateOnly day, CancellationToken ct = default) =>
        ReadAsync<JsonElement>(tenantId, $"v1/agenda/day?clinicalDay={day:yyyy-MM-dd}", ct);

    public static DateOnly ClinicalDay(DateTimeOffset instant, string timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant,TimeZoneInfo.FindSystemTimeZoneById(timeZone)).Date);

    public async Task<bool> IsSlotAvailableAsync(Guid tenantId,Guid doctor,DateTimeOffset start,int duration,string timeZone,CancellationToken ct=default)
    {
        ValidateAppointment(doctor,start,duration);
        var day=ClinicalDay(start,timeZone);
        var options=await GetAvailabilityAsync(tenantId,day,day,doctor,ct);
        return options.Professionals.Where(p=>p.ClinicianId==doctor).SelectMany(p=>p.Days).SelectMany(d=>d.Slots)
            .Any(slot=>slot.StartsAt==start&&slot.Offered&&slot.TakenBy==0&&slot.DurationMinutes>=duration);
    }

    public async Task<JsonElement> GetPatientAppointmentRangeAsync(Guid tenantId,Guid patientId,string phone,DateOnly from,DateOnly to,CancellationToken ct=default)
    {
        if(to<from||to.DayNumber-from.DayNumber>30)throw new ArgumentException("Consulta hasta 31 días de citas");
        await GetVerifiedPatientAsync(tenantId,patientId,phone,ct);
        return await ReadAsync<JsonElement>(tenantId,$"v1/agenda/patients/{patientId:D}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}",ct);
    }

    public async Task<JsonElement> GetPatientAppointmentsAsync(Guid tenantId, Guid patientId,
        string phone, DateOnly day, CancellationToken ct = default)
    {
        if(bool.TryParse(Tenant(tenantId)["UsePatientAgenda"],out var scoped)&&scoped){
            var page=await GetPatientAppointmentRangeAsync(tenantId,patientId,phone,day,day,ct);
            return JsonSerializer.SerializeToElement(page.GetProperty("rows").EnumerateArray().Select(x=>new{
                appointmentId=x.GetProperty("appointmentId").GetGuid(),startsAt=x.GetProperty("scheduledStart").GetDateTimeOffset(),
                durationMinutes=x.GetProperty("durationMinutes").GetInt32(),doctor=x.GetProperty("clinicianName").GetString(),status=x.GetProperty("status").GetString()
            }).ToArray(),Json);
        }
        await GetVerifiedPatientAsync(tenantId, patientId, phone, ct);
        var agenda = await GetAgendaDayAsync(tenantId, day, ct);
        // Filter inside the trusted adapter before anything reaches a patient or model.
        var rows = agenda.GetProperty("rows").EnumerateArray()
            .Where(x => x.GetProperty("patientId").GetGuid() == patientId)
            .Select(x => new
            {
                appointmentId = x.GetProperty("appointmentId").GetGuid(),
                startsAt = x.GetProperty("scheduledStart").GetDateTimeOffset(),
                durationMinutes = x.GetProperty("durationMinutes").GetInt32(),
                doctor = x.GetProperty("clinicianName").GetString(),
                status = x.GetProperty("status").GetString()
            }).ToArray();
        return JsonSerializer.SerializeToElement(rows, Json);
    }

    public async Task<HospitalAppointmentCreated> CreateAppointmentAsync(Guid tenantId,
        string senderPhone, HospitalAppointmentCreate input, CancellationToken ct = default)
    {
        await GetVerifiedPatientAsync(tenantId, input.PatientId, senderPhone, ct);
        ValidateAppointment(input.ClinicianId, input.StartsAt, input.DurationMinutes);
        if (input.VisitKind is not ("first-visit" or "follow-up" or "procedure" or "results" or "paperwork" or "same-day-urgent"))
            throw new ArgumentException("Unknown hospital visit kind.");
        // Hospital deliberately accepts attributed overlaps. The caller must surface Overlaps,
        // never blindly retry this POST after a timeout (hospital has no idempotency contract).
        using var response = await SendAsync(tenantId, HttpMethod.Post, "v1/agenda", input, ct);
        return await ParseAsync<HospitalAppointmentCreated>(response, ct);
    }

    public async Task RescheduleAppointmentAsync(Guid tenantId, Guid patientId, string senderPhone,
        Guid appointmentId, Guid clinicianId, DateTimeOffset startsAt, int durationMinutes,
        CancellationToken ct = default)
    {
        await VerifyAppointmentAsync(tenantId, patientId, senderPhone, appointmentId, ct);
        ValidateAppointment(clinicianId, startsAt, durationMinutes);
        using var response = await SendAsync(tenantId, HttpMethod.Post,
            $"v1/agenda/{appointmentId:D}/reschedule", new { startsAt, clinicianId, durationMinutes }, ct);
    }

    public async Task CancelAppointmentAsync(Guid tenantId, Guid patientId, string senderPhone,
        Guid appointmentId, CancellationToken ct = default, string reason = "patient-requested")
    {
        // The default is the agent's case: the patient asked over WhatsApp. Staff state theirs.
        var cancellation = AppointmentCancellation.From(reason);
        await VerifyAppointmentAsync(tenantId, patientId, senderPhone, appointmentId, ct);
        using var response = await SendAsync(tenantId, HttpMethod.Post,
            $"v1/agenda/{appointmentId:D}/cancel", new { reason = cancellation.Reason, cancelledByPatient = cancellation.CancelledByPatient }, ct);
    }

    public async Task<HospitalPrescriptionPage> ListIssuedPrescriptionsAsync(Guid tenantId,
        Guid patientId, string senderPhone, string? cursor = null, CancellationToken ct = default)
    {
        RequireClinicalDelivery(tenantId);
        await GetVerifiedPatientAsync(tenantId, patientId, senderPhone, ct);
        using var response = await SendAsync(tenantId, HttpMethod.Post,
            $"v1/reception/patients/{patientId:D}/prescriptions/list", new { phone = senderPhone, cursor }, ct);
        return await ParseAsync<HospitalPrescriptionPage>(response, ct);
    }

    public async Task<Guid?> GetLatestIssuedPrescriptionIdAsync(Guid tenantId, Guid patientId, string phone, CancellationToken ct = default)
    {
        // The bridge paginates timeline order, which need not equal signing order.
        HospitalPrescription? latest = null; string? cursor = null; var seen = new HashSet<string>();
        for (var pageNumber = 0; pageNumber < 100; pageNumber++)
        {
            var page = await ListIssuedPrescriptionsAsync(tenantId, patientId, phone, cursor, ct);
            foreach (var id in page.PrescriptionIds)
            {
                var prescription = await GetIssuedPrescriptionAsync(tenantId, patientId, phone, id, ct);
                if (latest is null || prescription.SignedAt > latest.SignedAt || (prescription.SignedAt == latest.SignedAt && prescription.PrescriptionId.CompareTo(latest.PrescriptionId) > 0)) latest = prescription;
            }
            if (string.IsNullOrEmpty(page.NextCursor)) return latest?.PrescriptionId;
            if (!seen.Add(page.NextCursor)) break;
            cursor = page.NextCursor;
        }
        throw new HospitalIntegrationException("hospital.prescription_history_incomplete", HttpStatusCode.BadGateway);
    }

    public async Task<HospitalPrescription> GetIssuedPrescriptionAsync(Guid tenantId,
        Guid patientId, string senderPhone, Guid prescriptionId, CancellationToken ct = default)
    {
        RequireClinicalDelivery(tenantId);
        await GetVerifiedPatientAsync(tenantId, patientId, senderPhone, ct);
        using var response = await SendAsync(tenantId, HttpMethod.Post,
            $"v1/reception/prescriptions/{prescriptionId:D}", new { phone = senderPhone }, ct);
        var prescription = await ParseAsync<HospitalPrescription>(response, ct);
        if (prescription.PrescriptionId != prescriptionId || prescription.PatientId != patientId
            || prescription.State != "signed" || prescription.ContentWithheld)
            throw new HospitalIntegrationException("hospital.prescription_unavailable", HttpStatusCode.NotFound);
        return prescription;
    }

    public async Task<byte[]> GetPrescriptionPdfAsync(Guid tenantId, Guid patientId,
        string senderPhone, Guid prescriptionId, CancellationToken ct = default)
    {
        // The bridge resolves the immutable owner from the prescription id. Bind that owner
        // to this CRM contact before requesting the PDF, including shared-phone households.
        await GetIssuedPrescriptionAsync(tenantId, patientId, senderPhone, prescriptionId, ct);
        // The bridge rechecks current phone and signed state before rendering.
        using var response = await SendAsync(tenantId, HttpMethod.Post,
            $"v1/reception/prescriptions/{prescriptionId:D}/pdf", new { phone = senderPhone }, ct);
        if (response.Content.Headers.ContentType?.MediaType != "application/pdf")
            throw new HospitalIntegrationException("hospital.invalid_pdf_response", HttpStatusCode.BadGateway);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task VerifyAppointmentAsync(Guid tenantId, Guid patientId, string phone,
        Guid appointmentId, CancellationToken ct)
    {
        await GetVerifiedPatientAsync(tenantId, patientId, phone, ct);
        var row = await ReadAsync<HospitalAppointmentRow>(tenantId,
            $"v1/agenda/{appointmentId:D}?patientId={patientId:D}", ct);
        if (row.PatientId != patientId || row.AppointmentId != appointmentId)
            throw new HospitalIntegrationException("hospital.appointment_unavailable", HttpStatusCode.NotFound);
    }

    private void RequireClinicalDelivery(Guid tenantId)
    {
        if (!bool.TryParse(Tenant(tenantId)["AllowClinicalDelivery"], out var allow) || !allow || !UseReceptionBridge(tenantId))
            throw new HospitalIntegrationException("hospital.clinical_delivery_not_authorized", HttpStatusCode.Forbidden);
    }

    private bool UseReceptionBridge(Guid tenantId) =>
        bool.TryParse(Tenant(tenantId)["UseReceptionBridge"], out var use) && use;

    private IConfigurationSection Tenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant is required.");
        return connections?.Section(tenantId) ?? configuration.GetSection($"Hospital:Tenants:{tenantId:D}");
    }

    private async Task<T> ReadAsync<T>(Guid tenantId, string path, CancellationToken ct)
    {
        using var response = await SendAsync(tenantId, HttpMethod.Get, path, null, ct);
        return await ParseAsync<T>(response, ct);
    }

    private static async Task<T> ParseAsync<T>(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync<T>(Json, ct)
        ?? throw new HospitalIntegrationException("hospital.empty_response", HttpStatusCode.BadGateway);

    private async Task<HttpResponseMessage> SendAsync(Guid tenantId, HttpMethod method,
        string path, object? body, CancellationToken ct)
    {
        var tenant = Tenant(tenantId);
        if (!Uri.TryCreate(tenant["BaseUrl"]?.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            throw new HospitalIntegrationException("hospital.not_configured", HttpStatusCode.ServiceUnavailable);
        var token = await GetTokenAsync(tenant, ct);
        ValidateTokenTenant(token, tenantId);
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            var code = $"hospital.http_{(int)status}";
            if(path.StartsWith("v1/commercial/",StringComparison.Ordinal))
            {
                try
                {
                    var problem=await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                    if(problem.TryGetProperty("code",out var value)&&value.GetString() is {} commercialCode && commercialCode.StartsWith("commercial.",StringComparison.Ordinal) && commercialCode.Length<100 && commercialCode.All(c=>char.IsAsciiLetterOrDigit(c)||c is '.' or '_'))code=commercialCode;
                }
                catch(JsonException) { }
            }
            response.Dispose();
            // Never put PHI-bearing response text, URLs or tokens into exception messages.
            throw new HospitalIntegrationException(code, status);
        }
        return response;
    }

    private async Task<string> GetTokenAsync(IConfigurationSection tenant, CancellationToken ct)
    {
        if (tenant["AccessToken"] is { Length: > 0 } token) return token;
        if (!Uri.TryCreate(tenant["TokenEndpoint"], UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment)
            || string.IsNullOrWhiteSpace(tenant["ClientId"]) || string.IsNullOrWhiteSpace(tenant["ClientSecret"]))
            throw new HospitalIntegrationException("hospital.credentials_missing", HttpStatusCode.ServiceUnavailable);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = tenant["ClientId"]!,
                ["client_secret"] = tenant["ClientSecret"]!
            })
        };
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HospitalIntegrationException("hospital.token_request_failed", HttpStatusCode.ServiceUnavailable);
        var payload = await ParseAsync<JsonElement>(response, ct);
        return payload.GetProperty("access_token").GetString()
            ?? throw new HospitalIntegrationException("hospital.token_missing", HttpStatusCode.ServiceUnavailable);
    }

    // This is an additional routing guard on configured credentials, NOT signature validation.
    // The hospital verifies the signature, audience and issuer with Keycloak on every request.
    internal static void ValidateTokenTenant(string token, Guid tenantId)
    {
        try
        {
            var segments = token.Split('.');
            if (segments.Length != 3) throw new FormatException();
            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = document.RootElement;
            if (!Guid.TryParse(root.GetProperty("tenant_id").GetString(), out var claimTenant) || claimTenant != tenantId
                || root.GetProperty("exp").GetInt64() <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 10)
                throw new FormatException();
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or OverflowException)
        {
            throw new HospitalIntegrationException("hospital.token_tenant_or_expiry_invalid", HttpStatusCode.ServiceUnavailable);
        }
    }

    public static string? NormalizePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // No guessed country code or suffix matching: ambiguous local numbers need correction.
        var digits = new string(value.Where(c => c is >= '0' and <= '9').ToArray());
        return digits.Length is >= 8 and <= 15 && digits[0] != '0' ? digits : null;
    }

    private static void ValidateAppointment(Guid clinicianId, DateTimeOffset startsAt, int durationMinutes)
    {
        if (clinicianId == Guid.Empty || startsAt <= DateTimeOffset.UtcNow || durationMinutes is < 5 or > 480)
            throw new ArgumentException("A clinician, future start and valid duration are required.");
    }
}

public sealed class HospitalIntegrationException(string code, HttpStatusCode statusCode) : Exception(code)
{
    public string Code { get; } = code;
    public string DisplayMessage => Code switch
    {
        "commercial.quote_changed" => "Los precios o el convenio cambiaron en Hospital. Vuelve a cotizar y revisa el total.",
        "commercial.customer_link_conflict" or "commercial.customer_company_conflict" => "El cliente o su empresa cambiaron en Hospital. Comprueba el vínculo y vuelve a cotizar.",
        "commercial.service_unavailable" or "commercial.company_unavailable" => "Un servicio o empresa ya no está disponible en Hospital. Actualiza la cotización.",
        "commercial.idempotency_conflict" => "Esta compra ya tiene otro registro en Hospital. Revisa su historial antes de continuar.",
        "commercial.invalid_purchase" or "commercial.invalid_input" or "commercial.invalid_lines" or "commercial.invalid_phone" => "Hospital rechazó los datos de la compra. Revisa los servicios y el teléfono del contacto.",
        "hospital.customer_identity_mismatch" => "El cliente Hospital no coincide con el teléfono o expediente de este contacto.",
        "hospital.not_configured" => "Tu cuenta del Hospital está reconocida. El administrador debe habilitar la agenda para este hospital desde la configuración de la plataforma.",
        "hospital.credentials_missing" or "hospital.token_request_failed" or "hospital.token_missing" or "hospital.token_tenant_or_expiry_invalid" => "No pudimos acceder a Hospital. Tu cuenta sigue vinculada; pide al administrador que revise la conexión.",
        "hospital.patient_phone_mismatch" => "El teléfono del expediente no coincide con el contacto. Corrige los datos en Hospital antes de vincular.",
        "hospital.http_403" or "hospital.clinical_delivery_not_authorized" => "Tu cuenta no tiene permiso para esta operación en Hospital. Consulta al administrador.",
        "hospital.http_404" or "hospital.prescription_unavailable" or "hospital.appointment_unavailable" => "No encontramos la cita o el documento en Hospital. Actualiza la información y vuelve a intentar.",
        "hospital.http_409" => "La información cambió en Hospital. Actualiza la agenda antes de continuar.",
        _ => "No pudimos completar la operación en Hospital. Revisa la conexión desde Mi hospital y vuelve a intentar."
    };
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed record HospitalPatient(Guid PatientId, string GivenNames, string FamilyNames, string? Phone, string? Email)
{
    public override string ToString() => $"HospitalPatient {PatientId}";
}
public sealed record HospitalAppointmentCreate(Guid PatientId, Guid ClinicianId, DateTimeOffset StartsAt,
    int DurationMinutes, string VisitKind);
public sealed record HospitalAppointmentCreated(Guid AppointmentId, string Status, bool Overlaps);
public sealed record HospitalAppointmentRow(Guid AppointmentId, Guid PatientId, string VisitKind, string Status);
public sealed record HospitalBookingOptions(string ClinicalDayFrom, string ClinicalDayTo, int MaxDaysPerQuery,
    string RollState, IReadOnlyList<HospitalProfessional> Professionals);
public sealed record HospitalProfessional(Guid ClinicianId, string ClinicianName, string PlaceName,
    int DefaultDurationMinutes, IReadOnlyList<HospitalBookingDay> Days);
public sealed record HospitalBookingDay(string ClinicalDay, string State, int TakenSlotCount,
    string? AbsenceReason, string? NextOpenDay, string UtcOffset, IReadOnlyList<HospitalSlot> Slots);
public sealed record HospitalSlot(string SlotId, DateTimeOffset StartsAt, int DurationMinutes,
    int TakenBy, bool Offered, string? Reason);
public sealed record HospitalPrescriptionPage(IReadOnlyList<Guid> PrescriptionIds, string? NextCursor);
public sealed record HospitalPrescription(Guid PrescriptionId, Guid PatientId, Guid EncounterId, string State,
    DateTimeOffset SignedAt, IReadOnlyList<JsonElement> Lines, bool ContentWithheld)
{
    public override string ToString() => $"HospitalPrescription {PrescriptionId} [{State}]";
}

/// <summary>What Recepción tells Hospital when an appointment is cancelled.</summary>
public sealed record AppointmentCancellation(string Reason, bool CancelledByPatient)
{
    /// <summary>Hospital's closed reason vocabulary. Only a patient's own request is
    /// attributed to the patient; every other reason is the hospital's cancellation.</summary>
    public static AppointmentCancellation From(string? reason) => reason switch
    {
        "patient-requested" => new(reason, true),
        "clinician-unavailable" or "clinic-closed" or "duplicate" or "other" => new(reason, false),
        _ => throw new ArgumentException("Indica el motivo de la cancelación."),
    };
}
