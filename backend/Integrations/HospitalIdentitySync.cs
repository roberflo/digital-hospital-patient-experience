using Microsoft.EntityFrameworkCore;
namespace Recepcion.Integrations;

public static class HospitalIdentitySync
{
    /// <summary>Best effort: copies Hospital's display name and time zone onto the tenant row. Writes
    /// (and audits) only when something changed; never throws because Hospital could not answer.</summary>
    public static async Task<bool> Run(CrmDb db, TenantScope t, CurrentUser u, HospitalClient h, ILogger log, CancellationToken ct = default)
    {
        try
        {
            var clinic = await h.GetClinicAsync(t.Id, ct);
            var tenant = await db.Tenants.SingleOrDefaultAsync(x => x.Id == t.Id, ct);
            if (tenant is null) return false;
            var changed = false;
            if (clinic.DisplayName is not null && clinic.DisplayName != tenant.Name) { tenant.Name = clinic.DisplayName; changed = true; }
            if (clinic.TimeZone is not null && clinic.TimeZone != tenant.TimeZone) { tenant.TimeZone = clinic.TimeZone; changed = true; }
            if (!changed) return false;
            CrmEndpoints.Audit(db, t, u, "hospital.identity_synced", t.Id);
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Hospital identity sync skipped for tenant {Tenant}: {Type}", t.Id, ex.GetType().Name);
            return false;
        }
    }
}
