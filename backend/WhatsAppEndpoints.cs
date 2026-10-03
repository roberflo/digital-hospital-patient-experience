using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

public static class WhatsAppEndpoints
{
    public static void MapWhatsApp(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/conversations", async (CrmDb db, CurrentUser user, IConfiguration config, string? phone, string? phoneNumberId, string? status,string? state,string? assignment,Guid? conversationId,Guid? contactId,Guid? channelId,string? priority,string? label,int? page) =>
        {
            var query=db.Conversations.Where(x=>(conversationId==null||x.Id==conversationId)&&(status==null||x.Status==status)&&(state==null||x.State==state)&&(contactId==null||x.ContactId==contactId)&&(channelId==null||x.ChannelId==channelId)&&(priority==null||x.Priority==priority));
            if(!string.IsNullOrEmpty(phone)){var hash=Rules.PhoneHash(phone,config["PHONE_HASH_KEY"]!);query=query.Where(x=>db.Contacts.Any(c=>c.Id==x.ContactId&&c.PhoneHash==hash));}
            if(!string.IsNullOrEmpty(phoneNumberId))query=query.Where(x=>db.Channels.Any(c=>c.Id==x.ChannelId&&c.PhoneNumberId==phoneNumberId));
            if(assignment=="mine")query=query.Where(x=>x.AssignedTo==user.Subject);
            if(assignment?.StartsWith("member:",StringComparison.Ordinal)==true){var subject=assignment[7..];query=query.Where(x=>x.AssignedTo==subject);}
            if(assignment=="unassigned")query=query.Where(x=>x.AssignedTo==null);
            if(!string.IsNullOrEmpty(label)){var needle=InboxWorkflow.Labels(label);query=query.Where(x=>(", "+x.Labels+", ").Contains(", "+needle+", "));}
            var conv=await query.OrderByDescending(x=>x.Priority=="urgent"?3:x.Priority=="high"?2:x.Priority=="normal"?1:0).ThenByDescending(x=>x.UpdatedAt).ThenBy(x=>x.Id).Skip((Math.Clamp(page??1,1,10000)-1)*100).Take(100).ToListAsync();
            var ids=conv.Select(x=>x.Id).ToArray();
            var unread=await db.Messages.Where(x=>ids.Contains(x.ConversationId)&&x.Sender=="patient"&&!x.IsHistory&&!db.ConversationReads.Any(r=>r.ConversationId==x.ConversationId&&r.Subject==user.Subject&&r.LastReadAt>=x.ReceivedAt)).GroupBy(x=>x.ConversationId).Select(g=>new{Id=g.Key,Count=g.Count()}).ToDictionaryAsync(x=>x.Id,x=>x.Count);
            var contacts = await db.Contacts.Where(x => conv.Select(v => v.ContactId).Contains(x.Id)).ToDictionaryAsync(x => x.Id);
            var channels = await db.Channels.ToDictionaryAsync(x => x.Id);
            return conv.Select(x => new { conversation = x, contact = contacts.GetValueOrDefault(x.ContactId), channel = channels.GetValueOrDefault(x.ChannelId),unreadCount=unread.GetValueOrDefault(x.Id) });
        });
        api.MapGet("/conversations/{id:guid}/messages", async (Guid id, CrmDb db) => await db.Conversations.AnyAsync(x => x.Id == id) ? Results.Ok(await db.Messages.Where(x => x.ConversationId == id).OrderByDescending(x => x.CreatedAt).Take(200).OrderBy(x => x.CreatedAt).ToListAsync()) : Results.NotFound());
        api.MapPost("/conversations/{id:guid}/messages", async (Guid id, SendInput b, HttpContext ctx, CrmDb db, TenantScope t, CurrentUser u, ConversationService svc) =>
        {
            var conv = await db.Conversations.SingleOrDefaultAsync(x => x.Id == id); if (conv is null) return Results.NotFound();
            using (var teamLease = await svc.Lock(t.Id)) using (var l = await svc.Lock(id)) { await db.Entry(conv).ReloadAsync(); TeamEndpoints.RequireEditable(conv,u); InboxWorkflow.SetState(conv,"open"); conv.Status = "human"; conv.AssignedTo = u.Subject; conv.Revision++; await db.SaveChangesAsync(); }
            var msg = await svc.Send(id, Rules.Required(b.Body, 4000), "human", ctx.Request.Headers["Idempotency-Key"].ToString(), actingSubject: u.Subject, actor: u); CrmEndpoints.Audit(db, t, u, "message.sent", msg.Id); await db.SaveChangesAsync(); return Results.Ok(msg);
        });
        api.MapPost("/conversations/{id:guid}/media", async (Guid id, HttpContext ctx, CrmDb db, TenantScope t, CurrentUser u, KapsoClient kapso, ConversationService svc) =>
        {
            var conv = await db.Conversations.SingleOrDefaultAsync(x => x.Id == id); if (conv is null) return Results.NotFound();
            TeamEndpoints.RequireEditable(conv,u);
            var form = await ctx.Request.ReadFormAsync(); var file = form.Files.GetFile("file"); if (file is null || file.Length == 0 || file.Length > 16 * 1024 * 1024) throw new ArgumentException("Archivo requerido, máximo 16 MB");
            var allowed = new[] { "application/pdf", "image/jpeg", "image/png", "audio/ogg", "audio/mpeg", "audio/mp4", "video/mp4" }; if (!allowed.Contains(file.ContentType)) throw new ArgumentException("Formato no permitido");
            var channel = await db.Channels.SingleAsync(x => x.Id == conv.ChannelId); if (!channel.Enabled) throw new ArgumentException("Este número está desactivado. Un administrador puede activarlo en WhatsApp.");
            kapso.RequireSending(manual: true);
            if (!Rules.WithinWindow(conv.LastInboundAt, DateTimeOffset.UtcNow)) throw new ArgumentException("Ventana de WhatsApp cerrada. Espera un mensaje del paciente o utiliza una plantilla aprobada desde Kapso.");
            await using var stream = file.OpenReadStream(); var mid = await kapso.Upload(channel.PhoneNumberId, stream, file.FileName, file.ContentType);
            using (var teamLease = await svc.Lock(t.Id)) using (var l = await svc.Lock(id)) { await db.Entry(conv).ReloadAsync(); TeamEndpoints.RequireEditable(conv,u); InboxWorkflow.SetState(conv,"open"); conv.Status = "human"; conv.AssignedTo = u.Subject; conv.Revision++; await db.SaveChangesAsync(); }
            var type = file.ContentType.Split('/')[0]; if (type == "application") type = "document";
            var msg = await svc.Send(id, Path.GetFileName(file.FileName), "human", ctx.Request.Headers["Idempotency-Key"].ToString(), mid, type, actingSubject: u.Subject, actor: u); CrmEndpoints.Audit(db, t, u, "media.sent", msg.Id); await db.SaveChangesAsync(); return Results.Ok(msg);
        }).DisableAntiforgery();
        api.MapGet("/messages/{id:guid}/media", async (Guid id, CrmDb db, KapsoClient kapso) =>
        {
            var m = await db.Messages.SingleOrDefaultAsync(x => x.Id == id); if (m?.MediaId is null) return Results.NotFound(); var media = await kapso.Download(m.MediaId); return Results.File(media.Bytes, media.Type, fileDownloadName: m.MediaName ?? "archivo", enableRangeProcessing: true);
        });
        api.MapPatch("/conversations/{id:guid}", async (Guid id, ConversationInput b, CrmDb db, CurrentUser u, TenantScope t, ConversationService svc) =>
        {
            using var teamLease = await svc.Lock(t.Id); using var lease = await svc.Lock(id); var conv = await db.Conversations.SingleOrDefaultAsync(x => x.Id == id); if (conv is null) return Results.NotFound();
            TeamEndpoints.RequireEditable(conv,u);
            if(b.ExpectedRevision is {} revision && revision != conv.Revision)return Results.Conflict(new{title="La conversación cambió. Actualiza antes de guardar."});
            if (b.Status is not ("human" or "agent" or "closed")) throw new ArgumentException("Estado inválido");
            if (b.AssignedTo != null && !await db.Members.AnyAsync(x => x.Subject == b.AssignedTo && !x.Disabled)) throw new ArgumentException("Usuario no disponible");
            if (!u.Supervisor && b.AssignedTo != null && b.AssignedTo != u.Subject) throw new AccessDeniedException();
            if(b.Status=="closed")InboxWorkflow.SetState(conv,"resolved");
            else if(b.Status=="agent"||conv.State=="resolved")InboxWorkflow.SetState(conv,"open");
            conv.Status = b.Status; conv.AssignedTo = b.AssignedTo; conv.Revision++; conv.UpdatedAt = DateTimeOffset.UtcNow;
            db.Activities.Add(new Activity { TenantId = t.Id, ContactId = conv.ContactId, ConversationId = id, Actor = u.Name, ActorRole = u.Role, ActorSubject = u.Subject, Kind = "handoff", Body = b.Status == "agent" ? "Atención automática reanudada." : b.Status == "closed" ? "Conversación cerrada." : "Atención humana solicitada. Revisa mensajes y acciones anteriores." });
            CrmEndpoints.Audit(db, t, u, "conversation." + b.Status, id); await db.SaveChangesAsync(); return Results.Ok(conv);
        });
        api.MapGet("/channels", async (CrmDb db) => await db.Channels.ToListAsync());
        api.MapPost("/channels", async (ChannelInput b, CrmDb db, CurrentUser u, TenantScope t, KapsoClient k, IConfiguration c) =>
        {
            u.RequireAdmin(); Rules.Required(b.PhoneNumberId, 50); var raw = await k.Platform(HttpMethod.Get, "whatsapp/phone_numbers/" + Uri.EscapeDataString(b.PhoneNumberId));
            var number = raw.TryGetProperty("data", out var d) ? d : raw;
            var customer = number.TryGetProperty("customer_id", out var cust) ? cust.GetString() : null;
            var tenant = await db.Tenants.SingleAsync(x => x.Id == t.Id);
            var expected = tenant.KapsoCustomerId ?? c[$"Kapso:Tenants:{t.Id}:CustomerId"];
            var initial = (c["BOOTSTRAP_TENANT_ID"] ?? DemoSeed.TenantId.ToString()) == t.Id.ToString() && b.PhoneNumberId == c["KAPSO_PHONE_NUMBER_ID"];
            if (u.Role != "platform_admin" && !initial && (string.IsNullOrEmpty(expected) || customer != expected)) throw new AccessDeniedException();
            if (b.DoctorId is not null && !await db.Members.AnyAsync(x => x.Subject == b.DoctorId && x.Role == "doctor" && !x.Disabled)) throw new ArgumentException("Selecciona un doctor activo de este hospital");
            var coexistence = number.TryGetProperty("is_coexistence", out var coexist) && coexist.ValueKind == JsonValueKind.True;
            if (b.Coexistence && !coexistence) throw new ArgumentException("Kapso todavía no reporta coexistencia activa para este número");
            var row = new Channel { TenantId = t.Id, Name = Rules.Required(b.Name), PhoneNumberId = b.PhoneNumberId, DoctorId = b.DoctorId, Coexistence = coexistence, Enabled = false, KapsoCustomerId = customer }; db.Add(row); CrmEndpoints.Audit(db, t, u, "channel.created", row.Id); await db.SaveChangesAsync(); return Results.Ok(row);
        });
        api.MapPatch("/channels/{id:guid}", async (Guid id, ChannelState b, CrmDb db, CurrentUser u, TenantScope t) => { u.RequireAdmin(); var row = await db.Channels.SingleOrDefaultAsync(x => x.Id == id); if (row is null) return Results.NotFound(); row.Enabled = b.Enabled; CrmEndpoints.Audit(db, t, u, "channel.enabled", id); await db.SaveChangesAsync(); return Results.Ok(row); });
        api.MapPost("/channels/onboarding", async (WhatsAppOnboarding onboarding, CancellationToken ct) => await onboarding.Start(ct));
        api.MapPost("/channels/sync", async (WhatsAppOnboarding onboarding, CancellationToken ct) => await onboarding.Sync(ct));
        app.MapPost("/webhooks/kapso", Receive);
    }
    static async Task<IResult> Receive(HttpContext ctx, CrmDb db, TenantScope scope, IConfiguration config)
    {
        if (ctx.Request.ContentLength > 2 * 1024 * 1024) return Results.StatusCode(413);
        using var buffer = new MemoryStream(); var bytes = new byte[8192]; int read;
        while ((read = await ctx.Request.Body.ReadAsync(bytes)) > 0) { if (buffer.Length + read > 2 * 1024 * 1024) return Results.StatusCode(413); buffer.Write(bytes, 0, read); }
        var body = buffer.ToArray(); if (!Rules.VerifySignature(body, ctx.Request.Headers["X-Webhook-Signature"].ToString(), config["KAPSO_WEBHOOK_SECRET"] ?? "")) return Results.Unauthorized();
        using var json = JsonDocument.Parse(body); var root = json.RootElement;
        var items = root.TryGetProperty("batch", out var batch) && batch.ValueKind == JsonValueKind.True ? root.GetProperty("data").EnumerateArray().ToArray() : [root];
        foreach (var item in items)
        {
            if (!item.TryGetProperty("phone_number_id", out var pid)) continue;
            var channel = await db.Channels.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.PhoneNumberId == pid.GetString()); if (channel is null) continue;
            scope.Id = channel.TenantId;
            channel.LastWebhookAt=DateTimeOffset.UtcNow;
            // A transaction-wide channel lock serializes duplicate deliveries and contact creation.
            await using var tx = await db.Database.BeginTransactionAsync(); var key = BitConverter.ToInt64(channel.Id.ToByteArray(), 0);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})");
            var eventName = ctx.Request.Headers["X-Webhook-Event"].ToString();
            var fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(item.GetRawText())));
            var receiptKey = ctx.Request.Headers["X-Idempotency-Key"].ToString() + ":" + eventName + ":" + fingerprint;
            if (await db.Receipts.AnyAsync(x => x.Key == receiptKey)) { await tx.CommitAsync(); continue; }
            db.Receipts.Add(new Receipt { TenantId = scope.Id, Key = receiptKey });
            if (item.TryGetProperty("message", out var msg) && item.TryGetProperty("conversation", out var conversation)) await Ingest(item, msg, conversation, channel, eventName, db, scope, config);
            await db.SaveChangesAsync(); await tx.CommitAsync(); db.ChangeTracker.Clear();
        }
        return Results.Ok(new { accepted = true });
    }
    static async Task Ingest(JsonElement root, JsonElement msg, JsonElement conversation, Channel ch, string evt, CrmDb db, TenantScope scope, IConfiguration config)
    {
        var external = msg.GetProperty("id").GetString()!; var k = msg.TryGetProperty("kapso", out var ka) ? ka : default;
        var status = Get(k, "status") ?? "received"; var existing = await db.Messages.SingleOrDefaultAsync(x => x.ExternalId == external);
        if (existing != null) { if (status == "read" || status == "failed" || (status == "delivered" && existing.Status != "read") || (status == "sent" && existing.Status is "sending" or "uncertain")) existing.Status = status; return; }
        var phone = Get(conversation, "phone_number") ?? Get(msg, "from") ?? Get(msg, "to"); if (string.IsNullOrEmpty(phone)) return;
        var hash = Rules.PhoneHash(phone, config["PHONE_HASH_KEY"]!); var contact = await db.Contacts.SingleOrDefaultAsync(x => x.PhoneHash == hash);
        if (contact is null) { contact = new Contact { TenantId = scope.Id, Name = Get(conversation, "contact_name") ?? "Paciente", Phone = Rules.Phone(phone), PhoneHash = hash }; db.Add(contact); db.Activities.Add(new Activity{TenantId=scope.Id,ContactId=contact.Id,Actor="WhatsApp",ActorRole="system",Kind="contact_created",Body="Contacto creado automáticamente al recibir su primera conversación de WhatsApp."}); }
        var conv = await db.Conversations.SingleOrDefaultAsync(x => x.ChannelId == ch.Id && x.ContactId == contact.Id);
        if (conv is null) { conv = new Conversation { TenantId = scope.Id, ChannelId = ch.Id, ContactId = contact.Id, ExternalId = Get(conversation, "id") ?? "", Status = (await db.Tenants.AnyAsync(x=>x.Id==scope.Id&&x.AgentEnabled))&&ch.Enabled?"agent":"human" }; db.Add(conv); db.Activities.Add(InboxWorkflow.Event(scope,conv,"WhatsApp","conversation_created","Conversación vinculada al contacto CRM.")); }
        // Serialize with in-flight sends so an early provider echo cannot create a second row.
        var conversationLock = BitConverter.ToInt64(conv.Id.ToByteArray(), 0);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({conversationLock})");
        if(db.Entry(conv).State!=EntityState.Added)await db.Entry(conv).ReloadAsync();
        var reconciled = await db.Messages.SingleOrDefaultAsync(x => x.ExternalId == external);
        if (reconciled is not null) { if (status == "read" || status == "failed" || (status == "delivered" && reconciled.Status != "read")) reconciled.Status = status; return; }
        var origin = Get(k, "origin"); var outbound = Get(k, "direction") == "outbound"; var history = origin == "history_sync"; var passive = k.ValueKind == JsonValueKind.Object && k.TryGetProperty("passive", out var pass) && pass.ValueKind == JsonValueKind.True;
        var type = Get(msg, "type") ?? "text"; var content = Get(k, "content") ?? (msg.TryGetProperty("text", out var text) ? Get(text, "body") : null) ?? "[Archivo recibido]";
        if (k.ValueKind == JsonValueKind.Object && k.TryGetProperty("transcript", out var transcript)) content = Get(transcript, "text") ?? content;
        string? media = null; if (msg.TryGetProperty(type, out var typeData)) media = Get(typeData, "id");
        var timestamp = long.TryParse(Get(msg, "timestamp"), out var unix) && unix > 0 && unix <= DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.UtcNow;
        db.Add(new Message { TenantId = scope.Id, ConversationId = conv.Id, ExternalId = external, Sender = outbound ? "human" : "patient", Body = content, Type = type, MediaId = media, MediaName=msg.TryGetProperty(type,out var mediaContent)?Get(mediaContent,"filename"):null, Status = status, CreatedAt = timestamp, IsHistory=history });
        if(!history){conv.LastMessage=content.Length>160?content[..160]:content;conv.UpdatedAt = DateTimeOffset.UtcNow;} conv.Revision++;
        if (outbound && origin is "business_app" or "other_app" or "meta_business_agent" || passive || evt == "whatsapp.thread.standby")
        {
            conv.Status = "human"; db.Add(new Activity { TenantId = scope.Id, ConversationId = conv.Id, ContactId = contact.Id, Kind = "handoff", Actor = "WhatsApp", ActorRole = "external", Body = "Atención externa detectada. Agente automático en pausa." });
        }
        if (!outbound && !history && !passive && evt == "whatsapp.message.received")
        {
            if (conv.LastInboundAt is null || timestamp > conv.LastInboundAt) conv.LastInboundAt = timestamp;
            if(InboxWorkflow.ReopenOnInbound(conv))db.Activities.Add(InboxWorkflow.Event(scope,conv,"WhatsApp","conversation_state","Conversación reabierta por un nuevo mensaje del cliente."));
            db.Add(new Job { TenantId = scope.Id, ConversationId = conv.Id, Key = "agent:" + external });
        }
    }
    internal static string? Get(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
public record SendInput(string Body);
public record ConversationInput(string Status, string? AssignedTo, long? ExpectedRevision = null);
public record ChannelInput(string Name, string PhoneNumberId, string? DoctorId, bool Coexistence);
public record ChannelState(bool Enabled);
