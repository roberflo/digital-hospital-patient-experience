using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Recepcion.Integrations;

/// <summary>Calls the hospital's existing v1 contracts. Configuration is keyed by the
/// authenticated CRM tenant, never by a URL or bearer token submitted by a patient.</summary>
public sealed partial class HospitalClient(HttpClient http, IConfiguration configuration, HospitalConnectionStore? connections = null, ILogger<HospitalClient>? log = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A copy that speaks to Hospital as the signed-in person (MR-11): their own Keycloak token, so Hospital's permissions and
    /// audit name them. Never cached and never refreshed here; the agent, which never calls this, keeps the service account.</summary>
    public HospitalClient AsUser(string bearer) => new(http, configuration, connections, log) { userBearer = bearer, correlation = correlation, KeysOnly = KeysOnly };
    /// <summary>A candidate connection (Save): the connection comes from <c>configuration</c>, only the signing key from the store.</summary>
    public bool KeysOnly { get; init; }
    string? userBearer;
    /// <summary>Refuses up front work that would outlive the person's token: midway it would fail request after request. No-op on the service account, which renews itself.</summary>
    public void RequireUserTokenFor(Guid tenantId, TimeSpan needed)
    {
        if (userBearer is null) return;
        long expires;
        try { expires = ValidateTokenTenant(userBearer, tenantId); }
        catch (HospitalIntegrationException) { throw new HospitalIntegrationException("hospital.user_token_rejected", HttpStatusCode.Forbidden); }
        if (DateTimeOffset.FromUnixTimeSeconds(expires) - DateTimeOffset.UtcNow < needed) throw new HospitalIntegrationException("hospital.user_session_expiring", HttpStatusCode.Conflict);
    }

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

    /// <summary>The visit kinds Hospital publishes for a new appointment.</summary>
    public static readonly string[] VisitKinds = ["first-visit", "follow-up", "procedure", "results", "paperwork", "same-day-urgent"];
    /// <summary>MR-42/MR-43 (decided by the owner 2026-10-17): «first-visit» when Hospital has no appointment of this patient, of any date, before
    /// <paramref name="before"/> that the patient came to (Hospital decides it: no-shows, unrecorded and cancelled ones do not count), else «follow-up». One read. A suggestion, not a
    /// write: when Hospital cannot answer (no such route yet, an error, no answer in time) it is «follow-up», as every appointment was before.</summary>
    public async Task<string> FirstOrFollowUpAsync(Guid tenantId, Guid patientId, DateTimeOffset before, CancellationToken ct = default)
    {
        try
        {
            var answer = await ReadAsync<JsonElement>(tenantId, $"v1/agenda/patients/{patientId:D}/earlier?before={Uri.EscapeDataString(before.ToString("O"))}", ct);
            return answer.GetProperty("exists").GetBoolean() ? "follow-up" : "first-visit";
        }
        // A person Hospital refused is told so (MR-11); anything else Hospital could not answer is a follow-up (MR-42).
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or HospitalIntegrationException { Code: not ("hospital.user_forbidden" or "hospital.user_token_rejected") } or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { return "follow-up"; }
    }

    /// <summary>MR-44: Hospital's count of this patient's explicit no-shows, total and since the last appointment they came to. The patient's
    /// history, so the link is verified first, as for their appointments.</summary>
    public async Task<HospitalAttendance> GetPatientAttendanceAsync(Guid tenantId, Guid patientId, string phone, CancellationToken ct = default)
    {
        await GetVerifiedPatientAsync(tenantId, patientId, phone, ct);
        // Fails closed: a count Hospital did not send, or one that cannot be, is never shown as one.
        HospitalAttendance? answer;
        try { answer = await ReadAsync<HospitalAttendance>(tenantId, $"v1/agenda/patients/{patientId:D}/attendance", ct); }
        catch (JsonException) { answer = null; }
        return answer is { NoShows: >= 0, ConsecutiveNoShows: >= 0 } && answer.ConsecutiveNoShows <= answer.NoShows ? answer
            : throw new HospitalIntegrationException("hospital.invalid_response", HttpStatusCode.BadGateway);
    }

    /// <summary>Only ever the patient-scoped agenda: the whole day's agenda carries every other patient (MR-12).</summary>
    public async Task<JsonElement> GetPatientAppointmentsAsync(Guid tenantId, Guid patientId,
        string phone, DateOnly day, CancellationToken ct = default)
    {
        var page = await GetPatientAppointmentRangeAsync(tenantId, patientId, phone, day, day, ct);
        // Isolation comes from Hospital's patient-scoped endpoint: its rows publish no patientId (openapi.v1.json PatientAgendaRowResponse),
        // so the filter below never removes a row from the real contract. It stays only while
        // AgentIntegrationTests.PatientAgendaNeverReturnsOtherPatientsOrIdentifiers feeds day-agenda rows (with patientId) to this path.
        return JsonSerializer.SerializeToElement(page.GetProperty("rows").EnumerateArray()
            .Where(x => !x.TryGetProperty("patientId", out var owner) || owner.GetGuid() == patientId)
            .Select(x => new
            {
                appointmentId = x.GetProperty("appointmentId").GetGuid(),
                startsAt = x.GetProperty("scheduledStart").GetDateTimeOffset(),
                durationMinutes = x.GetProperty("durationMinutes").GetInt32(),
                doctor = x.GetProperty("clinicianName").GetString(),
                status = x.GetProperty("status").GetString()
            }).ToArray(), Json);
    }

    /// <summary>MR-41: Hospital keeps a create's key per tenant and caller; the same key and body answers the original, another body is a
    /// 409 <c>*.idempotency_key_reused</c>. This is Hospital's own format, checked here so a key it would refuse is never sent.</summary>
    public static string IdempotencyKey(string key) =>
        System.Text.RegularExpressions.Regex.IsMatch(key, @"^[A-Za-z0-9:_-]{1,100}\z") ? key : throw new ArgumentException("Clave de idempotencia inválida.");

    public async Task<HospitalAppointmentCreated> CreateAppointmentAsync(Guid tenantId,
        string senderPhone, HospitalAppointmentCreate input, string idempotencyKey, CancellationToken ct = default)
    {
        IdempotencyKey(idempotencyKey);
        await GetVerifiedPatientAsync(tenantId, input.PatientId, senderPhone, ct);
        ValidateAppointment(input.ClinicianId, input.StartsAt, input.DurationMinutes);
        if (!VisitKinds.Contains(input.VisitKind))
            throw new ArgumentException("Unknown hospital visit kind.");
        // Hospital deliberately accepts attributed overlaps. The caller must surface Overlaps. Sent again with the same key after a timeout,
        // Hospital answers the original appointment (MR-41); nothing here retries it, and a reused key is the caller's to reconcile.
        using var response = await SendAsync(tenantId, HttpMethod.Post, "v1/agenda", input, ct, idempotencyKey);
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
        return await ListVerifiedPrescriptionsAsync(tenantId, patientId, senderPhone, cursor, ct);
    }

    private async Task<HospitalPrescriptionPage> ListVerifiedPrescriptionsAsync(Guid tenantId, Guid patientId, string senderPhone, string? cursor, CancellationToken ct)
    {
        using var response = await SendAsync(tenantId, HttpMethod.Post,
            $"v1/reception/patients/{patientId:D}/prescriptions/list", new { phone = senderPhone, cursor }, ct);
        return await ParseAsync<HospitalPrescriptionPage>(response, ct);
    }

    public async Task<Guid?> GetLatestIssuedPrescriptionIdAsync(Guid tenantId, Guid patientId, string phone, CancellationToken ct = default)
    {
        // The bridge paginates timeline order, which need not equal signing order.
        // The phone is verified once for the whole walk; the bridge rechecks it on every request.
        RequireClinicalDelivery(tenantId);
        await GetVerifiedPatientAsync(tenantId, patientId, phone, ct);
        HospitalPrescription? latest = null; string? cursor = null; var seen = new HashSet<string>();
        for (var pageNumber = 0; pageNumber < 100; pageNumber++)
        {
            var page = await ListVerifiedPrescriptionsAsync(tenantId, patientId, phone, cursor, ct);
            foreach (var id in page.PrescriptionIds)
            {
                var prescription = await GetVerifiedPrescriptionAsync(tenantId, patientId, phone, id, ct);
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
        return await GetVerifiedPrescriptionAsync(tenantId, patientId, senderPhone, prescriptionId, ct);
    }

    private async Task<HospitalPrescription> GetVerifiedPrescriptionAsync(Guid tenantId, Guid patientId, string senderPhone, Guid prescriptionId, CancellationToken ct)
    {
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
        return connections is not null && !KeysOnly ? connections.Section(tenantId) : configuration.GetSection($"Hospital:Tenants:{tenantId:D}");
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
        string path, object? body, CancellationToken ct, string? idempotencyKey = null)
    {
        var tenant = Tenant(tenantId);
        if (!Uri.TryCreate(tenant["BaseUrl"]?.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            throw new HospitalIntegrationException("hospital.not_configured", HttpStatusCode.ServiceUnavailable);
        var token = userBearer ?? await GetTokenAsync(tenantId, tenant, ct);
        // A person's token that is not this hospital's, or about to expire, is theirs to renew: a 403, never a 401 that loops the login.
        try { ValidateTokenTenant(token, tenantId); }
        catch (HospitalIntegrationException) when (userBearer is not null) { throw new HospitalIntegrationException("hospital.user_token_rejected", HttpStatusCode.Forbidden); }
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Correlation-Id", (correlation ?? Guid.NewGuid()).ToString("D"));
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        HttpResponseMessage response;
        // HttpClient.Timeout, not the caller giving up. Hospital may still have applied a write: never retried here.
        try { response = await http.SendAsync(request, ct); }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException && !ct.IsCancellationRequested)
        { throw new HospitalIntegrationException("hospital.timeout", HttpStatusCode.GatewayTimeout); }
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            var code = $"hospital.http_{(int)status}";
            if (userBearer is not null && status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                response.Dispose();
                throw new HospitalIntegrationException(status == HttpStatusCode.Forbidden ? "hospital.user_forbidden" : "hospital.user_token_rejected", HttpStatusCode.Forbidden);
            }
            // A token Hospital no longer accepts (revoked, rotated keys) is dropped; the next request asks for a new one, this one is not retried.
            // Only the entry holding the rejected token: one another request has just stored stays.
            if (status == HttpStatusCode.Unauthorized) foreach (var entry in Tokens.Where(e => e.Key.Tenant == tenantId && e.Value.Token == token)) Tokens.TryRemove(entry);
            var commercial=path.StartsWith("v1/commercial/",StringComparison.Ordinal);
            // A keyed create refused for its key is named (MR-41): the caller reconciles it instead of reading a generic 409.
            if(commercial || idempotencyKey is not null && status==HttpStatusCode.Conflict)
            {
                try
                {
                    var problem=await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                    if(problem.TryGetProperty("code",out var value)&&value.GetString() is {} named && (commercial
                        ? named.StartsWith("commercial.",StringComparison.Ordinal) && named.Length<100 && named.All(c=>char.IsAsciiLetterOrDigit(c)||c is '.' or '_')
                        : named is "scheduling.idempotency_key_reused" or "patient.idempotency_key_reused"))code=named;
                }
                catch(JsonException) { }
            }
            response.Dispose();
            // Never put PHI-bearing response text, URLs or tokens into exception messages.
            throw new HospitalIntegrationException(code, status);
        }
        return response;
    }

    // ponytail: one dictionary per process, never trimmed (one entry per hospital credential) and with no lock: two requests on a cold or
    // expiring entry both ask Keycloak, and each replica keeps its own. Keyed by tenant as well as credential, so a token never crosses
    // hospitals; the credential (signing key's kid, or the secret's hash) is in the key so a rotated one is tested, not answered from here.
    static readonly ConcurrentDictionary<(Guid Tenant, string Endpoint, string ClientId, string Secret), (string Token, DateTimeOffset Until)> Tokens = new();

    private async Task<string> GetTokenAsync(Guid tenantId, IConfigurationSection tenant, CancellationToken ct)
    {
        if (tenant["AccessToken"] is { Length: > 0 } token) return token;
        if (!Uri.TryCreate(tenant["TokenEndpoint"], UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment)
            || string.IsNullOrWhiteSpace(tenant["ClientId"]))
            throw new HospitalIntegrationException("hospital.credentials_missing", HttpStatusCode.ServiceUnavailable);
        var clientId = tenant["ClientId"]!; var secret = tenant["ClientSecret"];
        // MR-38: with a connection store the hospital signs with its own key (client-jwt); the audience is Keycloak's
        // public issuer, the only one it accepts even when reached through KEYCLOAK_INTERNAL_ISSUER (spike 2026-10-17).
        using var signing = connections is not null && configuration["Auth:Authority"] is { Length: > 0 } ? await connections.SigningKeyAsync(tenantId, ct: ct) : null;
        if (signing is null && string.IsNullOrWhiteSpace(secret))
            throw new HospitalIntegrationException("hospital.credentials_missing", HttpStatusCode.ServiceUnavailable);
        var key = (tenantId, endpoint.AbsoluteUri, clientId, signing is not null ? HospitalConnectionStore.Jwk(signing)["kid"] : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret!))));
        if (Tokens.TryGetValue(key, out var kept) && kept.Until > DateTimeOffset.UtcNow) return kept.Token;
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = clientId };
        HttpResponseMessage response;
        if (signing is not null)
        {
            response = await PostTokenAsync(endpoint, new(form) { ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer", ["client_assertion"] = ClientAssertion(signing, clientId, configuration["Auth:Authority"]!) }, ct);
            // A secret client (an account not yet turned into client-jwt by Hospital's reconciler, deploy step 2, or an explicit
            // per-hospital Hospital__Tenants__<id>__ClientSecret / PUT connection) answers the key with invalid_client: only then,
            // and only if this hospital has a secret, it is tried. Deploy step 3 removes the shared one; this branch stays.
            if (!string.IsNullOrWhiteSpace(secret) && await IsInvalidClientAsync(response, ct))
            {
                log?.LogWarning("hospital.token_fallback {Tenant}", tenantId);
                response.Dispose(); response = await PostTokenAsync(endpoint, new(form) { ["client_secret"] = secret }, ct);
            }
        }
        else response = await PostTokenAsync(endpoint, new(form) { ["client_secret"] = secret! }, ct);
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new HospitalIntegrationException("hospital.token_request_failed", HttpStatusCode.ServiceUnavailable);
            var payload = await ParseAsync<JsonElement>(response, ct);
            var issued = payload.GetProperty("access_token").GetString()
                ?? throw new HospitalIntegrationException("hospital.token_missing", HttpStatusCode.ServiceUnavailable);
            // Only a token already proven to be this tenant's is kept, and only until 30 s before it expires.
            Tokens[key] = (issued, DateTimeOffset.FromUnixTimeSeconds(ValidateTokenTenant(issued, tenantId)).AddSeconds(-30));
            return issued;
        }
    }

    private Task<HttpResponseMessage> PostTokenAsync(Uri endpoint, Dictionary<string, string> form, CancellationToken ct) =>
        http.SendAsync(new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(form) }, ct);

    private static async Task<bool> IsInvalidClientAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)) return false;
        try { return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).TryGetProperty("error", out var error) && error.GetString() == "invalid_client"; }
        catch (JsonException) { return false; }
    }

    /// <summary>RFC 7523 client authentication: ES256, iss = sub = the client, a one-minute life and a fresh jti.</summary>
    internal static string ClientAssertion(ECDsa key, string clientId, string audience)
    {
        static string B64(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signed = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "JWT", kid = HospitalConnectionStore.Jwk(key)["kid"] }))
            + "." + B64(JsonSerializer.SerializeToUtf8Bytes(new { iss = clientId, sub = clientId, aud = audience, jti = Guid.NewGuid().ToString("N"), iat = now, exp = now + 60 }));
        return signed + "." + B64(key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256));
    }

    // This is an additional routing guard on configured credentials, NOT signature validation.
    // The hospital verifies the signature, audience and issuer with Keycloak on every request.
    /// <returns>The token's expiry, in Unix seconds.</returns>
    internal static long ValidateTokenTenant(string token, Guid tenantId)
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
            return root.GetProperty("exp").GetInt64();
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
        "hospital.user_forbidden" => "Tu usuario no tiene permiso para esto en Hospital.",
        "hospital.user_session_expiring" => "Tu sesión está por vencer; recarga y vuelve a intentar.",
        "hospital.user_token_rejected" => "Hospital no aceptó tu sesión. Recarga la página; si continúa, consulta al administrador.",
        "hospital.http_403" or "hospital.clinical_delivery_not_authorized" => "Tu cuenta no tiene permiso para esta operación en Hospital. Consulta al administrador.",
        "hospital.http_404" or "hospital.prescription_unavailable" or "hospital.appointment_unavailable" => "No encontramos la cita o el documento en Hospital. Actualiza la información y vuelve a intentar.",
        "scheduling.idempotency_key_reused" or "patient.idempotency_key_reused" => "Hospital ya tiene esta solicitud registrada con otros datos. Comprueba la agenda del paciente antes de repetirla.",
        "hospital.http_409" => "La información cambió en Hospital. Actualiza la agenda antes de continuar.",
        "hospital.timeout" => "Hospital no respondió a tiempo. Comprueba la agenda antes de reintentar.",
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
