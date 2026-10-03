using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Recepcion.Integrations;

/// <summary>Calls the hospital's existing v1 contracts. Configuration is keyed by the
/// authenticated CRM tenant, never by a URL or bearer token submitted by a patient.</summary>
public sealed class HospitalClient(HttpClient http, IConfiguration configuration)
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

    public async Task<JsonElement> GetPatientAppointmentsAsync(Guid tenantId, Guid patientId,
        string phone, DateOnly day, CancellationToken ct = default)
    {
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
        Guid appointmentId, CancellationToken ct = default)
    {
        await VerifyAppointmentAsync(tenantId, patientId, senderPhone, appointmentId, ct);
        using var response = await SendAsync(tenantId, HttpMethod.Post,
            $"v1/agenda/{appointmentId:D}/cancel", new { reason = "patient-requested", cancelledByPatient = true }, ct);
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

    public async Task<HospitalPrescription> GetIssuedPrescriptionAsync(Guid tenantId,
        Guid patientId, string senderPhone, Guid prescriptionId, CancellationToken ct = default)
    {
        RequireClinicalDelivery(tenantId);
        await GetVerifiedPatientAsync(tenantId, patientId, senderPhone, ct);
        using var response = await SendAsync(tenantId, HttpMethod.Post,
            $"v1/reception/patients/{patientId:D}/prescriptions/{prescriptionId:D}", new { phone = senderPhone }, ct);
        var prescription = await ParseAsync<HospitalPrescription>(response, ct);
        if (prescription.PrescriptionId != prescriptionId || prescription.PatientId != patientId
            || prescription.State != "signed" || prescription.ContentWithheld)
            throw new HospitalIntegrationException("hospital.prescription_unavailable", HttpStatusCode.NotFound);
        return prescription;
    }

    public async Task<byte[]> GetPrescriptionPdfAsync(Guid tenantId, Guid patientId,
        string senderPhone, Guid prescriptionId, CancellationToken ct = default)
    {
        RequireClinicalDelivery(tenantId);
        await GetVerifiedPatientAsync(tenantId, patientId, senderPhone, ct);
        // The bridge validates prescription ownership and current signed state before rendering.
        using var response = await SendAsync(tenantId, HttpMethod.Post,
            $"v1/reception/patients/{patientId:D}/prescriptions/{prescriptionId:D}/pdf", new { phone = senderPhone }, ct);
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
        return configuration.GetSection($"Hospital:Tenants:{tenantId:D}");
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
            response.Dispose();
            // Never put PHI-bearing response text, URLs or tokens into exception messages.
            throw new HospitalIntegrationException($"hospital.http_{(int)status}", status);
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
