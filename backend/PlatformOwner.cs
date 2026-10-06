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
// connection and WhatsApp on its behalf. It is never a Member, never opens a hospital's space and
// never maps to admin: it only reaches routes marked PlatformOperable.
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
            // Never created here (INV-R4): the space is opened by the hospital's Administrador or BOOTSTRAP_TENANT_ID.
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

    public static void MapPlatformOwner(this WebApplication app) =>
        app.MapGet("/api/platform/tenants", Tenants).RequireAuthorization().WithMetadata(new PlatformOperable(NeedsTenant: false));
}
