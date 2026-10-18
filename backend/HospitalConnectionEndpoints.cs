using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
using System.Text.Json;
namespace Recepcion;

public static class HospitalConnectionEndpoints
{
    public static void MapHospitalConnection(this WebApplication app)
    {
        var api = app.MapGroup("/api/hospital").RequireAuthorization();
        api.MapGet("/connection/setup", Setup).WithMetadata(new PlatformOperable());
        api.MapPut("/connection", Save).WithMetadata(new PlatformOperable());
        api.MapGet("/connection", Read).WithMetadata(new PlatformOperable());
        api.MapPost("/connection/check", Check).WithMetadata(new PlatformOperable());
        // Not PlatformOperable: the platform owner's list is the spec's (docs/platform-owner.md). The hospital's Administrador rotates.
        api.MapPost("/connection/key", RotateKey);
        app.MapGet("/hospital-jwks/{tenant:guid}", Jwks).AllowAnonymous().RequireRateLimiting("jwks");
        // POST keeps patient search terms out of browser history and proxy request URLs.
        api.MapPost("/contacts/{id:guid}/patients/search", async (Guid id, HospitalPatientSearchInput input, HttpContext ctx, CrmDb db, TenantScope t, CurrentUser u, HospitalClient h, CancellationToken ct) =>
        {
            CommercialService.RequireOperator(u);
            if (!await db.Contacts.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var results = await h.AsUser(Identity.Bearer(ctx)).SearchPatients(t.Id, input.QueryShape, input.Term ?? "", ct);
            CrmEndpoints.Audit(db, t, u, "patient.searched", id);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { results });
        });
    }
    // MR-38: Keycloak's view of `recepcion-service-<tenant>`'s key. Anonymous and outside /api (Keycloak has no
    // session here); public halves only, and the same empty set for a hospital that has no key or does not exist.
    public static object Jwks(Guid tenant, HospitalConnectionStore store) => new { keys = store.PublicKeys(tenant, DateTimeOffset.UtcNow) };
    public static async Task<IResult> RotateKey(CurrentUser u, TenantScope t, CrmDb db, HospitalConnectionStore store, CancellationToken ct)
    {
            u.RequireAdmin();
            // The store takes the tenant's advisory lock (the one Save takes) around the rotation.
            (await store.SigningKeyAsync(t.Id, rotate: true, ct))?.Dispose();
            CrmEndpoints.Audit(db, t, u, "hospital.key_rotated", t.Id);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { rotated = true });
    }
    public static async Task<object> Read(CrmDb db, TenantScope t, CurrentUser u, HospitalClient h, HttpContext ctx, IConfiguration config) => new
        {
            hospital = await db.Tenants.Where(x => x.Id == t.Id).Select(x => new { x.Id, x.Name, x.TimeZone, x.AgentEnabled, x.RemindersEnabled }).SingleAsync(),
            configured = h.IsConfigured(t.Id), hospitalUrl = h.EntryUrl(t.Id, "/es"),
            sharedIdentity = ctx.User.FindFirst("iss")?.Value == config["Auth:Authority"] && !string.IsNullOrEmpty(config["Auth:Authority"]),
            hospitalLoginAvailable = !string.IsNullOrEmpty(config["Auth:Authority"]), u.Name, u.Role
        };
    public static IResult Setup(CurrentUser u, IConfiguration config)
    {
            u.RequireAdminOrPlatform();
            return Results.Ok(new { allowedOrigins = (config["HOSPITAL_ALLOWED_API_ORIGINS"]??"").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries), publicUrl=config["HOSPITAL_PUBLIC_URL"]??"" });
    }
    public static async Task<IResult> Save(HospitalConnectInput input, CurrentUser u, TenantScope t, CrmDb db, IConfiguration config, IHttpClientFactory factory, ConversationService locks, CancellationToken ct)
    {
            u.RequireAdminOrPlatform();
            var values = HospitalConnectionRules.Validate(input, config);
            var candidateConfig = new ConfigurationBuilder().AddInMemoryCollection(values.Select(x=>new KeyValuePair<string,string?>($"Hospital:Tenants:{t.Id}:"+x.Key,x.Value)).Append(new("Auth:Authority",config["Auth:Authority"]))).Build();
            using var http = factory.CreateClient("hospital-setup");
            // Signs with the hospital's key like every other token request (after deploy step 2 the account accepts
            // nothing else); the typed secret is only the fallback on invalid_client.
            var candidate = new HospitalClient(http, candidateConfig, new HospitalConnectionStore(db, config)) { KeysOnly = true };
            using var lease = await locks.Lock(t.Id);
            var tenant = await db.Tenants.SingleAsync(x=>x.Id==t.Id,ct);
            var day = HospitalClient.ClinicalDay(DateTimeOffset.UtcNow,tenant.TimeZone);
            // No persistence until Hospital accepts the credentials and exact authenticated tenant.
            await candidate.GetAvailabilityAsync(t.Id,day,day,ct:ct);
            // The hospital's signing keys survive a re-connection: Keycloak keeps trusting the published ones (MR-38).
            // Read after the check, which may have just created the first one.
            if (await db.Tenants.AsNoTracking().Where(x=>x.Id==t.Id).Select(x=>x.HospitalConnection).SingleAsync(ct) is { } current)
                foreach (var kept in JsonSerializer.Deserialize<Dictionary<string,string?>>(current)!.Where(x=>x.Key is "SigningKey" or "PreviousPublicKey" or "RotatedAt")) values[kept.Key] = kept.Value;
            tenant.HospitalConnection = JsonSerializer.Serialize(values);
            CrmEndpoints.Audit(db,t,u,"hospital.connected",t.Id);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { connected=true, checkedAt=DateTimeOffset.UtcNow });
    }
    public static async Task<IResult> Check(CrmDb db, TenantScope t, CurrentUser u, HospitalClient h, ILogger<HospitalClient> log, CancellationToken ct)
    {
            var zone = await db.Tenants.Where(x => x.Id == t.Id).Select(x => x.TimeZone).SingleAsync(ct);
            var day = HospitalClient.ClinicalDay(DateTimeOffset.UtcNow, zone);
            await h.GetAvailabilityAsync(t.Id, day, day, ct: ct);
            await HospitalIdentitySync.Run(db, t, u, h, log, ct);
            // An act done on a reception's behalf leaves a trace even when nothing changed (docs/platform-owner.md AC 13).
            if (u.Platform) { CrmEndpoints.Audit(db, t, u, "hospital.checked", t.Id); await db.SaveChangesAsync(ct); }
            return Results.Ok(new { connected = true, checkedAt = DateTimeOffset.UtcNow });
    }
}
public record HospitalPatientSearchInput(string QueryShape, string? Term);
