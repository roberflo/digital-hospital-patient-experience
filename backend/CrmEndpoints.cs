using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

public static class CrmEndpoints
{
    public static void Audit(CrmDb db, TenantScope t, CurrentUser u, string action, Guid id) => db.Audits.Add(new Audit { TenantId = t.Id, Actor = u.Subject, Action = action, Resource = id.ToString() });
    public static void MapCrm(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/me", async (CrmDb db, TenantScope t, CurrentUser u) => new { u.Subject, u.Name, u.Role, tenant = await db.Tenants.Where(x => x.Id == t.Id).Select(x => new { x.Id, x.Name, x.TimeZone, x.AgentEnabled }).SingleAsync() });
        api.MapGet("/overview", async (CrmDb db) => new { contacts = await db.Contacts.CountAsync(), conversations = await db.Conversations.CountAsync(x => x.State != "resolved"), human = await db.Conversations.CountAsync(x => x.Status == "human" && x.State == "open"), agent = await db.Conversations.CountAsync(x => x.Status == "agent" && x.State == "open"), opportunities = await db.Opportunities.CountAsync(x => x.Stage != "won" && x.Stage != "lost"), pending = await db.Jobs.CountAsync(x => x.Status == "pending"), failed = await db.Jobs.CountAsync(x => x.Status == "failed" || x.Status == "uncertain") });
        api.MapGet("/agent-metrics", (CrmDb db, int? days, CancellationToken ct) => AgentMetrics.Read(db, days, ct));
        api.MapGet("/contacts", async (CrmDb db, string? q, int? page) =>
        {
            var offset = (Math.Clamp(page ?? 1, 1, 10000) - 1) * 100;
            var query = db.Contacts.AsNoTracking().OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id);
            if (string.IsNullOrWhiteSpace(q)) return await query.Skip(offset).Take(100).ToListAsync();
            if (q.Length > 100) throw new ArgumentException("Búsqueda demasiado larga");
            // Names and phones are encrypted: stream scoped records without truncating the search.
            var matches = new List<Contact>(); var seen = 0;
            await foreach (var row in query.AsAsyncEnumerable())
            {
                if (!row.Name.Contains(q, StringComparison.OrdinalIgnoreCase) && !row.Phone.Contains(q)) continue;
                if (seen++ < offset) continue; matches.Add(row); if (matches.Count == 100) break;
            }
            return matches;
        });
        api.MapPost("/contacts", async (ContactInput b, CrmDb db, TenantScope t, CurrentUser u, IConfiguration c) =>
        {
            var row = new Contact { TenantId = t.Id }; SetContact(row, b, c); db.Add(row); Audit(db, t, u, "contact.created", row.Id); await db.SaveChangesAsync(); return Results.Created($"/api/contacts/{row.Id}", row);
        });
        api.MapPut("/contacts/{id:guid}", async (Guid id, ContactInput b, CrmDb db, TenantScope t, CurrentUser u, IConfiguration c, ConversationService locks) =>
        {
            using var contactLease = await locks.Lock(id);
            var row = await db.Contacts.SingleOrDefaultAsync(x => x.Id == id); if (row is null) return Results.NotFound();
            if (row.Phone != Rules.Phone(b.Phone))
            {
                if (row.HospitalCustomerId is not null || await db.Opportunities.AnyAsync(x=>x.ContactId==id&&x.HospitalPurchaseRequest!=null) || await db.Conversations.AnyAsync(x => x.ContactId == id)) throw new ArgumentException("Este teléfono tiene conversaciones. Crea otro contacto para conservar separado su historial.");
                row.PatientId = null;
            }
            SetContact(row, b, c); Audit(db, t, u, "contact.updated", id); await db.SaveChangesAsync(); return Results.Ok(row);
        });
        api.MapPost("/contacts/{id:guid}/patient", async (Guid id, PatientLinkInput b, CrmDb db, TenantScope t, CurrentUser u, CommercialService commercial) =>
        {
            var row = await db.Contacts.SingleOrDefaultAsync(x => x.Id == id); if (row is null) return Results.NotFound();
            await commercial.LinkPatient(row,b.PatientId,u); Audit(db, t, u, "patient.linked", id); await db.SaveChangesAsync(); return Results.Ok(row);
        });
        // Legacy clients receive an explicit instruction; master records live only in Hospital.
        api.MapGet("/companies",async(HospitalClient h,TenantScope t)=>(await h.Companies(t.Id,null,1)).Items);
        api.MapPost("/companies",()=>Results.Conflict(new{title="Administra empresas y convenios desde Hospital."}));
        api.MapPut("/companies/{id:guid}",()=>Results.Conflict(new{title="Administra empresas y convenios desde Hospital."}));
        api.MapGet("/opportunities", async (CrmDb db) => await db.Opportunities.OrderByDescending(x => x.UpdatedAt).Take(500).ToListAsync());
        api.MapPost("/opportunities", async (OpportunityInput b, CrmDb db, TenantScope t, CurrentUser u) =>
        {
            if (!await db.Contacts.AnyAsync(x => x.Id == b.ContactId)) return Results.NotFound();
            if(b.ConversationId is {} source&&!await db.Conversations.AnyAsync(x=>x.Id==source&&x.ContactId==b.ContactId))return Results.NotFound();
            ValidateStage(b.Stage); if (b.Value < 0) throw new ArgumentException("Valor inválido");
            var row = new Opportunity { TenantId = t.Id, ContactId = b.ContactId, ConversationId=b.ConversationId, Title = Rules.Required(b.Title), Value = b.Value, Stage = b.Stage }; db.Add(row);db.Add(new Activity{TenantId=t.Id,ContactId=row.ContactId,ConversationId=row.ConversationId,Kind="opportunity",Actor=u.Name,ActorRole=u.Role,ActorSubject=u.Subject,Body="Seguimiento creado: "+row.Title}); Audit(db, t, u, "opportunity.created", row.Id); await db.SaveChangesAsync(); return Results.Ok(row);
        });
        api.MapPatch("/opportunities/{id:guid}", async (Guid id, StageInput b, CrmDb db, TenantScope t, CurrentUser u) =>
        {
            var row = await db.Opportunities.SingleOrDefaultAsync(x => x.Id == id); if (row is null) return Results.NotFound(); ValidateStage(b.Stage); if(row.HospitalPurchaseId is not null&&b.Stage!="won")throw new ArgumentException("La compra pagada está registrada en Hospital y conserva su etapa ganada."); row.Stage = b.Stage; row.UpdatedAt = DateTimeOffset.UtcNow; db.Add(new Activity{TenantId=t.Id,ContactId=row.ContactId,ConversationId=row.ConversationId,Kind="opportunity",Actor=u.Name,ActorRole=u.Role,ActorSubject=u.Subject,Body="Etapa de seguimiento actualizada: "+row.Title+" → "+b.Stage}); Audit(db, t, u, "opportunity.stage", id); await db.SaveChangesAsync(); return Results.Ok(row);
        });
        api.MapGet("/activities", async (CrmDb db, Guid? contactId, Guid? conversationId) => await db.Activities.Where(x => x.Kind != "intake" && (contactId == null || x.ContactId == contactId) && (conversationId == null || x.ConversationId == conversationId)).OrderByDescending(x => x.CreatedAt).Take(150).ToListAsync());
        api.MapPost("/activities", async (ActivityInput b, CrmDb db, TenantScope t, CurrentUser u) =>
        {
            if (b.ContactId is { } cid && !await db.Contacts.AnyAsync(x => x.Id == cid)) return Results.NotFound();
            var contactId=b.ContactId;
            if (b.ConversationId is { } vid){var conversation=await db.Conversations.SingleOrDefaultAsync(x=>x.Id==vid);if(conversation is null||contactId is {} linked&&linked!=conversation.ContactId)return Results.NotFound();contactId=conversation.ContactId;}
            var row = new Activity { TenantId = t.Id, ContactId = contactId, ConversationId = b.ConversationId, Body = Rules.Required(b.Body, 10000), Kind = "note", Actor = u.Name, ActorRole = u.Role, ActorSubject = u.Subject }; db.Add(row); Audit(db, t, u, "activity.created", row.Id); await db.SaveChangesAsync(); return Results.Ok(row);
        });
        api.MapGet("/members", async (CrmDb db) => await db.Members.Select(x => new { x.Subject, x.Name, x.Role, x.Disabled }).ToListAsync());
        api.MapPatch("/members/{id}", async (string id, MemberInput b, CrmDb db, CurrentUser u, TenantScope t, ConversationService locks) =>
        {
            u.RequireAdmin(); using var teamLease = await locks.Lock(t.Id); if (id == u.Subject) throw new ArgumentException("No puedes desactivar tu propia cuenta"); var row = await db.Members.SingleOrDefaultAsync(x => x.Subject == id); if (row is null) return Results.NotFound(); if(b.Disabled && await db.Conversations.AnyAsync(c=>c.AssignedTo==id&&c.State!="resolved"))return Results.Conflict(new{title="Reasigna las conversaciones activas de esta persona antes de desactivar su acceso."}); row.Disabled = b.Disabled; Audit(db, t, u, "member.access", row.Id); await db.SaveChangesAsync(); return Results.Ok();
        });
        api.MapGet("/settings", async (CrmDb db, TenantScope t, CurrentUser u, HospitalClient h, IConfiguration c) =>
        {
            u.RequireAdmin(); var tenant = await db.Tenants.SingleAsync(x => x.Id == t.Id);
            return new { tenant.Name, tenant.Guide, tenant.EmergencyPhone, tenant.TimeZone, tenant.AgentEnabled, tenant.GoogleCalendarId, googleConnected = tenant.GoogleRefreshToken != null, hospitalConfigured = h.IsConfigured(t.Id), kapsoConfigured = !string.IsNullOrEmpty(c["KAPSO_API_KEY"]), aiConfigured = !string.IsNullOrEmpty(c["NVIDIA_API_KEY"]), sendEnabled = c["SEND_ENABLED"] == "true", manualSendEnabled = c["SEND_ENABLED"] == "true" || c["KAPSO_MANUAL_SEND_ENABLED"] == "true", aiModel = c["AI_MODEL"] ?? "nvidia/nemotron-3-super-120b-a12b" };
        });
        api.MapPut("/settings", async (SettingsInput b, CrmDb db, TenantScope t, CurrentUser u, HospitalClient h, CancellationToken ct) =>
        {
            u.RequireAdmin(); if (b.Guide.Length > 30000) throw new ArgumentException("Guía demasiado extensa");
            try { TimeZoneInfo.FindSystemTimeZoneById(b.TimeZone); } catch (TimeZoneNotFoundException) { throw new ArgumentException("Zona horaria inválida"); }
            var row = await db.Tenants.SingleAsync(x => x.Id == t.Id);
            // Hospital owns name and zone only if it answers with a valid name right now; otherwise the admin's edit stands.
            var owned = await HospitalOwnsIdentity(row, h, TimeSpan.FromSeconds(3), ct);
            ApplySettings(row, b, owned);
            Audit(db, t, u, "settings.updated", t.Id); await db.SaveChangesAsync(); return Results.Ok();
        });
        api.MapGet("/audit", async (CrmDb db, CurrentUser u) => { u.RequireAdmin(); return await db.Audits.OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(); });
        api.MapGet("/jobs", async (CrmDb db, CurrentUser u) => { u.RequireAdmin(); return await db.Jobs.OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(); });
    }
    /// <summary>True when Hospital answers within the deadline with a valid name; its name (and zone, if valid) are then copied onto the row.</summary>
    public static async Task<bool> HospitalOwnsIdentity(Tenant row, HospitalClient h, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
            var clinic = await h.GetClinicAsync(row.Id, deadline.Token);
            if (clinic.DisplayName is null) return false;
            row.Name = clinic.DisplayName; if (clinic.TimeZone is not null) row.TimeZone = clinic.TimeZone;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return false; }
    }
    // A connected hospital's name and zone belong to Hospital; a manual edit would be overwritten at the next sync.
    public static void ApplySettings(Tenant row, SettingsInput b, bool hospitalConnected)
    {
        var name = Rules.Required(b.Name);
        // Short codes (911, 132) are valid emergency numbers, so this is not Rules.Phone.
        var emergency = b.EmergencyPhone?.Trim(); if (emergency is { Length: > 0 } && (emergency.Length is < 3 or > 20 || emergency.Any(c => !char.IsAsciiDigit(c) && c is not ('+' or ' ' or '-')))) throw new ArgumentException("Teléfono de urgencias inválido");
        row.EmergencyPhone = string.IsNullOrEmpty(emergency) ? null : emergency;
        if (!hospitalConnected) { row.Name = name; row.TimeZone = b.TimeZone; }
        row.Guide = b.Guide; row.AgentEnabled = b.AgentEnabled;
    }
    static void SetContact(Contact row, ContactInput b, IConfiguration c) { row.Name = Rules.Required(b.Name); row.Phone = Rules.Phone(b.Phone); row.PhoneHash = Rules.PhoneHash(b.Phone, c["PHONE_HASH_KEY"]!); row.Email = b.Email ?? ""; row.Tags = b.Tags ?? ""; if (row.Email.Length > 320 || row.Tags.Length > 500) throw new ArgumentException("Campo demasiado largo"); }
    static void ValidateStage(string stage) { if (stage is not ("new" or "contacted" or "scheduled" or "won" or "lost")) throw new ArgumentException("Etapa inválida"); }
}
public record ContactInput(string Name, string Phone, string? Email, string? Tags);
public record CompanyInput(string Name, string? Industry, string? Email, string? Phone);
public record OpportunityInput(string Title, Guid ContactId, decimal Value, string Stage, Guid? ConversationId=null);
public record StageInput(string Stage);
public record ActivityInput(string Body, Guid? ContactId, Guid? ConversationId);
public record PatientLinkInput(Guid PatientId);
public record MemberInput(bool Disabled);
public record SettingsInput(string Name, string Guide, string TimeZone, bool AgentEnabled, string? GoogleCalendarId, string? EmergencyPhone = null);
