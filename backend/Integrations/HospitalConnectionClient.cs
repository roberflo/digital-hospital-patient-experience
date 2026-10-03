namespace Recepcion.Integrations;

public sealed partial class HospitalClient
{
    public string? PublicUrl(Guid tenantId)
    {
        var value = Tenant(tenantId)["PublicUrl"];
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.GetLeftPart(UriPartial.Authority) : null;
    }

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
