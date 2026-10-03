using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
namespace Recepcion;

public sealed class CurrentUser
{
    public string Subject { get; set; } = ""; public string Role { get; set; } = ""; public string Name { get; set; } = "";
    public bool Admin => Role is "admin" or "platform_admin";
    public bool Supervisor => Admin || Role == "supervisor";
    public void RequireAdmin() { if (!Admin) throw new AccessDeniedException(); }
    public void RequireSupervisor() { if (!Supervisor) throw new AccessDeniedException(); }
}
public sealed class AccessDeniedException : Exception;
public static class Identity
{
    public static string? MapRole(ClaimsPrincipal user)
    {
        var roles = user.FindAll("role").Select(c => c.Value).ToList();
        var realm = user.FindFirst("realm_access")?.Value;
        if (realm is not null) { try { using var j = JsonDocument.Parse(realm); if (j.RootElement.ValueKind == JsonValueKind.Object && j.RootElement.TryGetProperty("roles", out var a) && a.ValueKind == JsonValueKind.Array) roles.AddRange(a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)); } catch (JsonException) { return null; } }
        if (roles.Contains("platform_admin")) return "platform_admin";
        if (roles.Any(r => r is "admin" or "Administrador")) return "admin";
        if (roles.Any(r => r is "supervisor" or "Supervisor")) return "supervisor";
        if (roles.Any(r => r is "doctor" or "Médicos" or "Odontólogos" or "Nutricionistas")) return "doctor";
        if (roles.Any(r => r is "agent" or "Recepción" or "Admisión")) return "agent";
        return null;
    }
    public static string DevToken(string subject, string name, string role, Guid tenant, IConfiguration config) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        issuer: "recepcion-dev", audience: "recepcion", claims: [new("sub", subject), new("name", name), new("role", role), new("tenant_id", tenant.ToString())],
        expires: DateTime.UtcNow.AddHours(1), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["DEV_JWT_KEY"]!)), SecurityAlgorithms.HmacSha256)));
    public static async Task<bool> Bind(HttpContext ctx, CrmDb db, TenantScope scope, CurrentUser current)
    {
        if (ctx.User.Identity?.IsAuthenticated != true) return false;
        if (!Guid.TryParse(ctx.User.FindFirst("tenant_id")?.Value, out var tenant)) return false;
        var sub = ctx.User.FindFirst("sub")?.Value; var role = MapRole(ctx.User);
        if (string.IsNullOrEmpty(sub) || role is null) return false;
        scope.Id = tenant;
        if (!await db.Tenants.AnyAsync(t => t.Id == tenant)) return false;
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
        current.Subject = sub; current.Name = member.Name; current.Role = role;
        if (member.Role != role) { member.Role = role; await db.SaveChangesAsync(); }
        return true;
    }
}
public static class DemoSeed
{
    public static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    public static readonly Guid SecondTenantId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    public static async Task Run(CrmDb db, TenantScope scope)
    {
        if (await db.Tenants.AnyAsync()) return;
        foreach (var id in new[] { TenantId, SecondTenantId })
        {
            scope.Id = id;
            db.Tenants.Add(new Tenant { Id = id, Name = id == TenantId ? "Hospital Demo · datos sintéticos" : "Clínica de prueba B", Guide = "Atención de lunes a viernes de 7:00 a 18:00. Para urgencias, orientar a servicios de emergencia. No confirmar precios sin consultar recepción.", AgentEnabled = false });
            await db.SaveChangesAsync();
            if (id != TenantId) continue;
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
