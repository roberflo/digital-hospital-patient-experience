using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Recepcion.Integrations;

// Staff clinical reads use the authenticated doctor's token, never the reception service account.
// Hospital remains responsible for signature validation, patient authorization and clinical audit.
public sealed class HospitalClinicalClient(HttpClient http, IConfiguration config, HospitalConnectionStore? connections = null)
{
    public async Task<JsonElement> ReadAsync(Guid tenant, string subject, string bearer, Guid patient,
        string phone, string section, Guid? documentId = null, string? cursor = null, CancellationToken ct = default)
    {
        var path = section switch
        {
            "timeline" when documentId is null => $"v1/patients/{patient}/timeline?limit=30" + Cursor(cursor),
            "antecedentes" or "allergies" when documentId is null => $"v1/patients/{patient}/{section}",
            "prescriptions" when documentId is not null => $"v1/prescriptions/{documentId}",
            "notes" when documentId is not null => $"v1/patients/{patient}/notes/{documentId}",
            _ => throw new ArgumentException("Consulta clínica no válida")
        };
        HospitalClient.ValidateTokenTenant(bearer, tenant);
        ValidateDoctor(bearer, subject);
        var configured = connections?.Section(tenant)["BaseUrl"] ?? config[$"Hospital:Tenants:{tenant}:BaseUrl"];
        if (string.IsNullOrWhiteSpace(configured) || !Uri.TryCreate(configured.TrimEnd('/') + "/", UriKind.Absolute, out var origin)
            || origin.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            throw new HospitalIntegrationException("hospital.not_configured", HttpStatusCode.ServiceUnavailable);

        // Recheck the link under the doctor's own Hospital authorization before every disclosure.
        var identity = await Get($"v1/patients/{patient}");
        RequireId(identity, "patientId", patient);
        if (HospitalClient.NormalizePhone(phone) is not { } normalized
            || !identity.TryGetProperty("phone", out var linkedPhone)
            || HospitalClient.NormalizePhone(linkedPhone.GetString()) != normalized)
            throw new HospitalIntegrationException("hospital.patient_phone_mismatch", HttpStatusCode.NotFound);
        var result = await Get(path);
        if (section == "prescriptions")
        {
            RequireId(result, "patientId", patient);
            RequireId(result, "prescriptionId", documentId!.Value);
            // Withheld/terminal states are returned as Hospital supplied them; UI must label them.
            if (result.GetProperty("contentWithheld").GetBoolean())
            {
                var redacted = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(result.GetRawText())!;
                redacted["lines"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
                result = JsonSerializer.SerializeToElement(redacted);
            }
        }
        if (section == "notes")
        {
            RequireId(result.GetProperty("note"), "patientId", patient);
            RequireId(result.GetProperty("note"), "noteId", documentId!.Value);
        }
        if (section == "antecedentes") RequireId(result, "patientId", patient);
        return result;

        async Task<JsonElement> Get(string relative)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // A rejected Hospital identity is not a CRM session expiration (no login loop).
                var status = response.StatusCode == HttpStatusCode.Unauthorized ? HttpStatusCode.Forbidden : response.StatusCode;
                throw new HospitalIntegrationException(status == HttpStatusCode.Forbidden
                    ? "hospital.doctor_access_required" : $"hospital.http_{(int)status}", status);
            }
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        }
    }

    static void RequireId(JsonElement value, string property, Guid expected)
    {
        if (!value.TryGetProperty(property, out var id) || !id.TryGetGuid(out var actual) || actual != expected)
            throw new HospitalIntegrationException("hospital.document_unavailable", HttpStatusCode.NotFound);
    }
    static string Cursor(string? cursor)
    {
        if (cursor is null) return "";
        if (cursor.Length > 2048) throw new ArgumentException("Cursor inválido");
        return "&cursor=" + Uri.EscapeDataString(cursor);
    }
    static void ValidateDoctor(string bearer, string subject)
    {
        try
        {
            var segment = bearer.Split('.')[1].Replace('-', '+').Replace('_', '/');
            using var document = JsonDocument.Parse(Convert.FromBase64String(segment.PadRight((segment.Length + 3) / 4 * 4, '=')));
            var payload = document.RootElement;
            if (!Guid.TryParse(subject, out var actor) || actor == Guid.Empty || payload.GetProperty("sub").GetString() != subject
                || !payload.GetProperty("realm_access").GetProperty("roles").EnumerateArray()
                    .Any(r => r.GetString() is "Médicos" or "Odontólogos" or "Nutricionistas")) throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new HospitalIntegrationException("hospital.doctor_login_required", HttpStatusCode.Forbidden);
        }
    }
}
