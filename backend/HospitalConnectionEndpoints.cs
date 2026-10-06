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
        // POST keeps patient search terms out of browser history and proxy request URLs.
        api.MapPost("/contacts/{id:guid}/patients/search", async (Guid id, HospitalPatientSearchInput input, CrmDb db, TenantScope t, CurrentUser u, HospitalClient h, CancellationToken ct) =>
        {
            CommercialService.RequireOperator(u);
            if (!await db.Contacts.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var results = await h.SearchPatients(t.Id, input.QueryShape, input.Term ?? "", ct);
            CrmEndpoints.Audit(db, t, u, "patient.searched", id);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { results });
        });
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
            var candidateConfig = new ConfigurationBuilder().AddInMemoryCollection(values.Select(x=>new KeyValuePair<string,string?>($"Hospital:Tenants:{t.Id}:"+x.Key,x.Value))).Build();
            using var http = factory.CreateClient("hospital-setup");
            var candidate = new HospitalClient(http, candidateConfig);
            using var lease = await locks.Lock(t.Id);
            var tenant = await db.Tenants.SingleAsync(x=>x.Id==t.Id,ct);
            var day = HospitalClient.ClinicalDay(DateTimeOffset.UtcNow,tenant.TimeZone);
            // No persistence until Hospital accepts the credentials and exact authenticated tenant.
            await candidate.GetAvailabilityAsync(t.Id,day,day,ct:ct);
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
