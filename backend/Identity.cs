using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
namespace Recepcion;

public sealed class CurrentUser
{
    public string Subject { get; set; } = ""; public string Role { get; set; } = ""; public string Name { get; set; } = "";
    public bool Admin => Role == "admin";
    public void RequireAdmin() { if (!Admin) throw new AccessDeniedException(); }
}
public sealed class AccessDeniedException : Exception;
public static class Identity
{
    public static string? MapRole(ClaimsPrincipal user)
    {
        // Hospital's Keycloak realm is the only issuer, and its seven PRD §3 roles the only vocabulary.
        // Enfermería has no Recepción workspace: it maps to nothing and is denied.
        var roles = new List<string>();
        var realm = user.FindFirst("realm_access")?.Value;
        if (realm is not null) { try { using var j = JsonDocument.Parse(realm); if (j.RootElement.ValueKind == JsonValueKind.Object && j.RootElement.TryGetProperty("roles", out var a) && a.ValueKind == JsonValueKind.Array) roles.AddRange(a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)); } catch (JsonException) { return null; } }
        if (roles.Contains("Administrador")) return "admin";
        if (roles.Any(r => r is "Médicos" or "Odontólogos" or "Nutricionistas")) return "doctor";
        if (roles.Any(r => r is "Recepción" or "Admisión")) return "agent";
        return null;
    }
    public static async Task<bool> Bind(HttpContext ctx, CrmDb db, TenantScope scope, CurrentUser current, IConfiguration? config = null)
    {
        if (ctx.User.Identity?.IsAuthenticated != true) return false;
        if (!Guid.TryParse(ctx.User.FindFirst("tenant_id")?.Value, out var tenant) || tenant == Guid.Empty) return false;
        var sub = ctx.User.FindFirst("sub")?.Value; var role = MapRole(ctx.User);
        if (string.IsNullOrEmpty(sub) || role is null) return false;
        scope.Id = tenant;
        if (!await db.Tenants.AnyAsync(t => t.Id == tenant))
        {
            // The hospital's own Administrador opens its space by signing in; no operator step.
            // HOSPITAL_SELF_ONBOARDING=false returns that to the operator (BOOTSTRAP_TENANT_ID).
            if (config is null || config["HOSPITAL_SELF_ONBOARDING"] == "false" || role != "admin" ||
                ctx.User.FindFirst("iss")?.Value != config["Auth:Authority"] ||
                await db.Members.IgnoreQueryFilters().AnyAsync(x=>x.Subject==sub)) return false;
            var hospital = new Tenant { Id=tenant, Name="Hospital · configura tu nombre", AgentEnabled=false };
            db.Tenants.Add(hospital);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException ex) when(ex.InnerException is Npgsql.PostgresException { SqlState:Npgsql.PostgresErrorCodes.UniqueViolation }) { db.Entry(hospital).State=EntityState.Detached; }
        }
        // Global subject uniqueness prevents a signed user from drifting to a second business.
        var member = await db.Members.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Subject == sub);
        if (member is null)
        {
            member = new Member { TenantId = tenant, Subject = sub, Name = ctx.User.FindFirst("name")?.Value ?? sub, Role = role }; db.Add(member);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
            {
                // A first login fans out into concurrent reads. Reuse the winning registration,
                // then apply the same tenant/disabled checks; never turn that race into a 409.
                db.Entry(member).State = EntityState.Detached;
                member = await db.Members.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Subject == sub);
                if (member is null) throw;
            }
        }
        if (member.TenantId != tenant || member.Disabled) return false;
        var name = ctx.User.FindFirst("name")?.Value ?? member.Name;
        if (member.Role != role || member.Name != name) { member.Role = role; member.Name = name; await db.SaveChangesAsync(); }
        current.Subject = sub; current.Name = member.Name; current.Role = role;
        return true;
    }
}
public static class DemoSeed
{
    // The synthetic tenants A and B of Hospital's dev realm (configure-realms.sh): a demo space is
    // only reachable by the `dev-<rol>-<a|b>` users Keycloak issues tokens for.
    public static readonly Guid TenantId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    public static readonly Guid SecondTenantId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    public static async Task Run(CrmDb db, TenantScope scope)
    {
        foreach (var id in new[] { TenantId, SecondTenantId })
        {
            scope.Id = id;
            if (await db.Tenants.AnyAsync(t => t.Id == id)) continue;
            db.Tenants.Add(new Tenant { Id = id, Name = id == TenantId ? "Hospital Demo · datos sintéticos" : "Clínica de prueba B", Guide = "Atención de lunes a viernes de 7:00 a 18:00. Para urgencias, orientar a servicios de emergencia. No confirmar precios sin consultar recepción.", AgentEnabled = false });
            // One save per tenant: a tenant row without its demo data would never be seeded again.
            if (id != TenantId) { await db.SaveChangesAsync(); continue; }
            var people = new[] { ("Ana Martínez", "50370000001", "Control"), ("Carlos Rivera", "50370000002", "Nuevo paciente"), ("María López", "50370000003", "Seguimiento"), ("José Hernández", "50370000004", "Receta") };
            var channel = new Channel { TenantId = id, Name = "Recepción · demostración", PhoneNumberId = "demo", Enabled = false }; db.Add(channel);
            foreach (var (name, phone, tag) in people)
            {
                var c = new Contact { TenantId = id, Name = name, Phone = phone, PhoneHash = "demo-" + phone, Tags = tag }; db.Add(c);
                var conversation = new Conversation { TenantId = id, ContactId = c.Id, ChannelId = channel.Id, Status = "human", LastInboundAt = DateTimeOffset.UtcNow, Summary = "Datos sintéticos para explorar el flujo de atención." }; db.Add(conversation);
                db.Add(new Message { TenantId = id, ConversationId = conversation.Id, Body = "Hola, quisiera información para mi próxima consulta.", Sender = "patient" });
                db.Add(new Activity { TenantId = id, ContactId = c.Id, ConversationId = conversation.Id, Body = "Conversación de demostración. Vincula un paciente real del hospital antes de consultar información clínica.", Actor = "Sistema", ActorRole = "system" });
                db.Add(new Opportunity { TenantId = id, ContactId = c.Id, Title = "Seguimiento · " + name, Stage = tag == "Control" ? "scheduled" : "new" });
            }
            db.Add(new Company { TenantId = id, Name = "Convenio de demostración", Industry = "Salud", Email = "convenio@example.invalid" });
            await db.SaveChangesAsync();
        }
    }
}
