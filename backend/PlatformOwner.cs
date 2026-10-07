using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

/// <summary>Marks a route the platform owner may operate (docs/platform-owner.md, INV-R2). Every route
/// without it answers 403 to that actor. <paramref name="NeedsTenant"/>: the route acts on the
/// reception named by <c>X-Acting-Tenant</c>.</summary>
public sealed record PlatformOperable(bool NeedsTenant = true);
public record PlatformTenant(Guid Id, string Name, bool HospitalConfigured, bool WhatsAppConnected);
public record PlatformTenants(IReadOnlyList<PlatformTenant> Tenants);

// The platform owner (realm role `platform-owner`, no `tenant_id`) configures a reception's Hospital
// connection and WhatsApp on its behalf, and may open the reception of a hospital that exists in Hospital
// (REC-3). It is never a Member and never maps to admin: it only reaches routes marked PlatformOperable.
public static class PlatformOwner
{
    public const string Role = "platform-owner";
    public const string Header = "X-Acting-Tenant";
    public static bool Is(ClaimsPrincipal user) => Identity.Roles(user).Contains(Role);

    /// <summary>Runs before <see cref="Identity.Bind"/>. <c>null</c>: not a platform request, continue to
    /// Bind. <c>0</c>: admitted, scope and CurrentUser are set and Bind must be skipped (no Member, no
    /// Tenant). Any other value is the status to answer.</summary>
    public static async Task<int?> Gate(HttpContext ctx, CrmDb db, TenantScope scope, CurrentUser current)
    {
        var chosen = ctx.Request.Headers.ContainsKey(Header);
        // The header on a hospital token is refused, not ignored (INV-R5): the tenant of a hospital user only ever comes from its JWT.
        if (!Is(ctx.User)) return chosen ? 403 : null;
        if (ctx.User.FindFirst("tenant_id") is not null) return 401;
        var sub = ctx.User.FindFirst("sub")?.Value;
        // Deny by default (INV-R2), decided before any query: an unmarked route, or no route at all.
        if (string.IsNullOrEmpty(sub) || ctx.GetEndpoint()?.Metadata.GetMetadata<PlatformOperable>() is not { } mark) return 403;
        if (mark.NeedsTenant)
        {
            if (!chosen) return 403;
            var acting = ctx.Request.Headers[Header];
            // Never created by choosing (INV-R4): a space is opened by its Administrador, BOOTSTRAP_TENANT_ID or POST /api/platform/tenants.
            if (acting.Count != 1 || !Guid.TryParseExact(acting[0], "D", out var tenant) || tenant == Guid.Empty || !await db.Tenants.AnyAsync(t => t.Id == tenant, ctx.RequestAborted)) return 404;
            scope.Id = tenant;
        }
        current.Subject = "platform:" + sub; current.Role = "platform"; current.Name = ctx.User.FindFirst("name")?.Value is { Length: > 0 } name ? name : "Dueño de plataforma";
        return 0;
    }

    /// <summary>The receptions the owner can choose from: no secrets, no guide, no connection values.</summary>
    public static async Task<PlatformTenants> Tenants(CurrentUser u, CrmDb db, HospitalClient h)
    {
        if (!u.Platform) throw new AccessDeniedException();
        var rows = await db.Tenants.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id).Select(x => new { x.Id, x.Name }).ToListAsync();
        var connected = (await db.Channels.IgnoreQueryFilters().Where(x => x.PhoneNumberId != "demo").Select(x => x.TenantId).Distinct().ToListAsync()).ToHashSet();
        // ponytail: IsConfigured reads one row per reception; fold into the query above if receptions reach the hundreds.
        return new(rows.Select(x => new PlatformTenant(x.Id, x.Name, h.IsConfigured(x.Id), connected.Contains(x.Id))).ToList());
    }

    /// <summary>REC-3: the hospitals Hospital lists for the owner, each marked with whether its reception is open.</summary>
    public static async Task<PlatformHospitals> Hospitals(CurrentUser u, HttpContext ctx, CrmDb db, IConfiguration config, IHttpClientFactory factory, CancellationToken ct)
    {
        if (!u.Platform) throw new AccessDeniedException();
        var listed = await ListedByHospital(ctx, config, factory, ct);
        var ids = listed.Keys.ToArray();
        var open = (await db.Tenants.Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        return new(listed.Select(x => new PlatformHospital(x.Key, x.Value, open.Contains(x.Key))).OrderBy(x => x.Name, StringComparer.InvariantCultureIgnoreCase).ThenBy(x => x.Id).ToList());
    }

    /// <summary>REC-3: opens the reception (Tenant) of a hospital that exists in Hospital, in the state a first
    /// sign-in of its Administrador leaves it. Never a Member: the Administrador still joins by signing in.</summary>
    public static async Task<IResult> Open(OpenTenantInput input, CurrentUser u, HttpContext ctx, CrmDb db, TenantScope scope, HospitalClient h, IConfiguration config, IHttpClientFactory factory, CancellationToken ct)
    {
        if (!u.Platform) throw new AccessDeniedException();
        if (!Guid.TryParse(input.HospitalId, out var id) || id == Guid.Empty) return Results.NotFound();
        static IResult AlreadyOpen() => Results.Conflict(new { title = "Este hospital ya tiene su recepción abierta.", code = "already_open" });
        if (await db.Tenants.AnyAsync(x => x.Id == id, ct)) return AlreadyOpen();
        if (!(await ListedByHospital(ctx, config, factory, ct)).ContainsKey(id)) return Results.NotFound();
        HospitalClinic clinic;
        // The hospital's own service account (`recepcion-service-<id>`), the path the connection check uses: if it cannot
        // get a token or is not yet let in, Hospital's reconciler has not finished creating it. Nothing is created.
        try { clinic = await h.GetClinicAsync(id, ct); }
        catch (HospitalIntegrationException ex) when (ex.Code is "hospital.token_request_failed" or "hospital.token_missing" or "hospital.token_tenant_or_expiry_invalid" or "hospital.http_401" or "hospital.http_403")
        { return Results.Conflict(new { title = "La cuenta de servicio de este hospital se está creando. Vuelve a intentarlo en un minuto.", code = "service_account_pending" }); }
        // Same row Identity.Bind creates on a hospital's first sign-in, with the name and zone its sync would copy next.
        var tenant = new Tenant { Id = id, Name = clinic.DisplayName ?? "Hospital · configura tu nombre", AgentEnabled = false };
        if (clinic.TimeZone is not null) tenant.TimeZone = clinic.TimeZone;
        scope.Id = id; db.Tenants.Add(tenant); CrmEndpoints.Audit(db, scope, u, "tenant.opened", id);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation }) { return AlreadyOpen(); }
        return Results.Created("/api/platform/tenants", new PlatformOpened(id, tenant.Name));
    }

    // Hospital's administrative list for the platform owner, read with the owner's own bearer (its token carries the
    // hospital-api audience). The token only travels to the installation's Hospital API, and only if that origin is allowed.
    static async Task<Dictionary<Guid, string>> ListedByHospital(HttpContext ctx, IConfiguration config, IHttpClientFactory factory, CancellationToken ct)
    {
        static HospitalIntegrationException Unreadable(string code) => new(code, System.Net.HttpStatusCode.BadGateway);
        var allowed = (config["HOSPITAL_ALLOWED_API_ORIGINS"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!Uri.TryCreate(config["HOSPITAL_API_URL"]?.TrimEnd('/') + "/", UriKind.Absolute, out var api) || api.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(api.UserInfo) || !string.IsNullOrEmpty(api.Query) || !string.IsNullOrEmpty(api.Fragment)
            || !allowed.Any(x => Uri.TryCreate(x, UriKind.Absolute, out var origin) && origin.GetLeftPart(UriPartial.Authority) == api.GetLeftPart(UriPartial.Authority)))
            throw Unreadable("hospital.platform_origin_not_allowed");
        if (!System.Net.Http.Headers.AuthenticationHeaderValue.TryParse(ctx.Request.Headers.Authorization, out var bearer) || !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(bearer.Parameter))
            throw Unreadable("hospital.platform_unreadable");
        try
        {
            using var http = factory.CreateClient("hospital-setup");
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(api, "v1/platform/hospitals"));
            request.Headers.Authorization = new("Bearer", bearer.Parameter);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw Unreadable("hospital.platform_unreadable");
            using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            // A hospital without a usable name is still listed: an empty or partial list must never look like «there are none».
            return doc.RootElement.GetProperty("hospitals").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetGuid(),
                x => x.TryGetProperty("displayName", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String && name.GetString()!.Trim() is { Length: > 0 and <= 200 } text ? text : "Hospital sin nombre");
        }
        // Never the response text, the URL or the token in the message.
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        { throw Unreadable("hospital.platform_unreadable"); }
    }

    public static void MapPlatformOwner(this WebApplication app)
    {
        app.MapGet("/api/platform/tenants", Tenants).RequireAuthorization().WithMetadata(new PlatformOperable(NeedsTenant: false));
        app.MapGet("/api/platform/hospitals", Hospitals).RequireAuthorization().WithMetadata(new PlatformOperable(NeedsTenant: false));
        app.MapPost("/api/platform/tenants", Open).RequireAuthorization().WithMetadata(new PlatformOperable(NeedsTenant: false));
    }
}
public record PlatformHospital(Guid Id, string Name, bool HasReception);
public record PlatformHospitals(IReadOnlyList<PlatformHospital> Hospitals);
// Only the id: the name and zone of a reception come from Hospital, never from the caller.
public record OpenTenantInput(string? HospitalId);
public record PlatformOpened(Guid Id, string Name);
