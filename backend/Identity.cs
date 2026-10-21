using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
namespace Recepcion;

public sealed class CurrentUser
{
    public string Subject { get; set; } = ""; public string Role { get; set; } = ""; public string Name { get; set; } = "";
    public bool Admin => Role == "admin";
    /// <summary>The platform owner acting for a reception (docs/platform-owner.md). Never an admin.</summary>
    public bool Platform => Role == "platform";
    public void RequireAdmin() { if (!Admin) throw new AccessDeniedException(); }
    /// <summary>Only the routes the platform owner may operate use this; everything else keeps RequireAdmin.</summary>
    public void RequireAdminOrPlatform() { if (!Admin && !Platform) throw new AccessDeniedException(); }
}
public sealed class AccessDeniedException : Exception;
public static class Identity
{
    /// <summary>The signed-in person's own bearer token, for calls Hospital must attribute to them.</summary>
    public static string Bearer(HttpContext ctx) =>
        System.Net.Http.Headers.AuthenticationHeaderValue.TryParse(ctx.Request.Headers.Authorization, out var auth)
        && auth.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(auth.Parameter)
            ? auth.Parameter : throw new AccessDeniedException();
    /// <summary>The realm roles of a token; a malformed `realm_access` yields none.</summary>
    public static List<string> Roles(ClaimsPrincipal user)
    {
        var roles = new List<string>();
        var realm = user.FindFirst("realm_access")?.Value;
        if (realm is not null) { try { using var j = JsonDocument.Parse(realm); if (j.RootElement.ValueKind == JsonValueKind.Object && j.RootElement.TryGetProperty("roles", out var a) && a.ValueKind == JsonValueKind.Array) roles.AddRange(a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)); } catch (JsonException) { } }
        return roles;
    }
    public static string? MapRole(ClaimsPrincipal user)
    {
        // Hospital's Keycloak realm is the only issuer, and its seven PRD §3 roles the only vocabulary.
        // Enfermería has no Recepción workspace: it maps to nothing and is denied.
        var roles = Roles(user);
        // The platform owner is never a teammate of any hospital, even holding Administrador too (INV-R3).
        if (roles.Contains(PlatformOwner.Role)) return null;
        // Hospital's per-tenant service accounts hold Recepción + reception-agent: a machine is never a teammate.
        if (roles.Contains("reception-agent") || user.FindFirst("preferred_username")?.Value.StartsWith("service-account-", StringComparison.Ordinal) == true) return null;
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
        var mode = await db.Tenants.Where(t => t.Id == tenant).Select(t => t.Mode).SingleOrDefaultAsync();
        // A hospital on the general number has no Recepción: nobody of it signs in or becomes a Member (recordatorios-whatsapp.spec.md criterio 17).
        if (mode == "reminders") { ctx.Items["hospital-reminders-only"] = true; return false; }
        if (mode is null)
        {
            // The hospital's own Administrador opens its space by signing in; no operator step.
            // HOSPITAL_SELF_ONBOARDING=false returns that to the operator (BOOTSTRAP_TENANT_ID).
            if (config is null || config["HOSPITAL_SELF_ONBOARDING"] == "false" || role != "admin" ||
                ctx.User.FindFirst("iss")?.Value != config["Auth:Authority"] ||
                await db.Members.IgnoreQueryFilters().AnyAsync(x=>x.Subject==sub))
            {
                // Anyone but the Administrador arriving first is told why, not just refused.
                if (role != "admin") ctx.Items["hospital-pending"] = true;
                return false;
            }
            var hospital = new Tenant { Id=tenant, Name="Hospital · configura tu nombre", AgentEnabled=false };
            db.Tenants.Add(hospital);
            try { await db.SaveChangesAsync(); ctx.Items["tenant-created"] = true; }
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
    public const string DentalName = "Clínica Dental Bonilla · datos sintéticos";
    /// <summary>
    /// Local demo only: the second synthetic tenant is a dental clinic and Hospital's local clinic an
    /// endocrinology one. A space without contacts gets its demo data, so an existing local database is filled too.
    /// </summary>
    public static async Task Specialties(CrmDb db, TenantScope scope, Guid? hospitalTenant)
    {
        var dental = new (string, string, string, string, string)[] {
            ("Sofía Ramírez", "50371000001", "Ortodoncia", "Hola, quisiera cotizar brackets para mi hija de 13 años.", "Ortodoncia · Sofía Ramírez"),
            ("Diego Flores", "50371000002", "Limpieza", "Buenos días, ¿tienen cita para limpieza dental esta semana?", "Limpieza dental · Diego Flores"),
            ("Valeria Campos", "50371000003", "Urgencia", "Se me quebró una muela y tengo mucho dolor, ¿me pueden atender hoy?", "Extracción de muela · Valeria Campos"),
            ("Ricardo Molina", "50371000004", "Blanqueamiento", "¿Qué precio tiene el blanqueamiento dental y cuántas sesiones son?", "Blanqueamiento · Ricardo Molina") };
        var endocrine = new (string, string, string, string, string)[] {
            ("Gloria Menjívar", "50372000001", "Diabetes", "Hola, necesito mi control de diabetes; ya tengo el resultado de la hemoglobina glicosilada.", "Control de diabetes · Gloria Menjívar"),
            ("Óscar Portillo", "50372000002", "Tiroides", "Buenas tardes, me indicaron exámenes de tiroides, ¿puedo llevarlos a la consulta?", "Seguimiento de tiroides · Óscar Portillo"),
            ("Patricia Guzmán", "50372000003", "Nuevo paciente", "Quisiera una primera consulta con el endocrinólogo, ¿qué horarios tienen?", "Primera consulta · Patricia Guzmán"),
            ("Mauricio Escobar", "50372000004", "Receta", "Se me está terminando la insulina, ¿me pueden renovar la receta?", "Renovación de receta · Mauricio Escobar") };
        var spaces = new List<(Guid, string, string, (string, string, string, string, string)[])> {
            (SecondTenantId, "Recepción dental · demostración", "Seguro dental de demostración", dental) };
        if (hospitalTenant is { } local && local != TenantId && local != SecondTenantId)
            spaces.Add((local, "Recepción endocrinología · demostración", "Laboratorio clínico de demostración", endocrine));
        foreach (var (id, channelName, companyName, people) in spaces)
        {
            scope.Id = id;
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id);
            if (tenant is null || await db.Contacts.AnyAsync()) continue;
            if (id == SecondTenantId && tenant.Name == "Clínica de prueba B") tenant.Name = DentalName;
            var channel = new Channel { TenantId = id, Name = channelName, PhoneNumberId = "demo-" + id.ToString("N")[..8], Enabled = false }; db.Add(channel);
            foreach (var (name, phone, tag, message, title) in people)
            {
                var c = new Contact { TenantId = id, Name = name, Phone = phone, PhoneHash = "demo-" + phone, Tags = tag }; db.Add(c);
                var conversation = new Conversation { TenantId = id, ContactId = c.Id, ChannelId = channel.Id, Status = "human", LastInboundAt = DateTimeOffset.UtcNow, Summary = "Datos sintéticos para explorar el flujo de atención." }; db.Add(conversation);
                db.Add(new Message { TenantId = id, ConversationId = conversation.Id, Body = message, Sender = "patient" });
                db.Add(new Activity { TenantId = id, ContactId = c.Id, ConversationId = conversation.Id, Body = "Conversación de demostración. Vincula un paciente real antes de consultar información clínica.", Actor = "Sistema", ActorRole = "system" });
                db.Add(new Opportunity { TenantId = id, ContactId = c.Id, Title = title, Stage = tag is "Limpieza" or "Diabetes" ? "scheduled" : "new" });
            }
            db.Add(new Company { TenantId = id, Name = companyName, Industry = "Salud", Email = "convenio@example.invalid" });
            await db.SaveChangesAsync();
        }
    }
}
