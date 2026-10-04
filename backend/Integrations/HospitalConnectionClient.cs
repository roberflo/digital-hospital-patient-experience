namespace Recepcion.Integrations;

public sealed partial class HospitalClient
{
    public string? PublicUrl(Guid tenantId)
    {
        var value = Tenant(tenantId)["PublicUrl"];
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.GetLeftPart(UriPartial.Authority) : null;
    }

    // Links into Hospital enter through its Keycloak sign-in, never straight to the page: Hospital
    // keeps its own session cookie, and a browser still signed in there as another hospital would
    // otherwise open THAT hospital from this one's workspace. Through the identity provider the
    // person arrives as who they are here, in the same hospital, without typing anything.
    public string? EntryUrl(Guid tenantId, string path) =>
        PublicUrl(tenantId) is { } origin ? origin + "/api/auth/idp?returnTo=" + Uri.EscapeDataString(path) : null;

    public async Task<HospitalPatientHit[]> SearchPatients(Guid tenantId, string shape, string term, CancellationToken ct = default)
    {
        if (shape is not ("name-tokens" or "dui" or "record-number")) throw new ArgumentException("Selecciona nombre, DUI o expediente");
        term = term.Trim();
        if (term.Length is < 3 or > 100) throw new ArgumentException("Escribe entre 3 y 100 caracteres para buscar");
        var page = await ReadAsync<HospitalPatientSearch>(tenantId, $"v1/patients/search?queryShape={shape}&term={Uri.EscapeDataString(term)}", ct);
        // No clinical history, document identifiers or unrelated demographics leave this boundary.
        return page.Results.Take(30).ToArray();
    }
}
public record HospitalPatientHit(Guid PatientId, string DisplayName, string? RecordNumber);
public record HospitalPatientSearch(HospitalPatientHit[] Results);
