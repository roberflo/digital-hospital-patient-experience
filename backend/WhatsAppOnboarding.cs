using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

// All customer/number identities come from the authenticated tenant and Kapso API,
// never from a browser callback or a caller-supplied customer_id.
public sealed class WhatsAppOnboarding(CrmDb db, TenantScope scope, CurrentUser user, KapsoClient kapso, IConfiguration config)
{
    public static readonly string[] Events = ["whatsapp.message.received", "whatsapp.message.sent", "whatsapp.message.delivered", "whatsapp.message.read", "whatsapp.message.failed", "whatsapp.conversation.created", "whatsapp.conversation.ended", "whatsapp.thread.standby"];
    public async Task<SetupResult> Start(CancellationToken ct)
    {
        user.RequireAdminOrPlatform();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock(ct);
        var tenant = await db.Tenants.SingleAsync(x => x.Id == scope.Id, ct);
        var customer = tenant.KapsoCustomerId ?? config[$"Kapso:Tenants:{scope.Id}:CustomerId"];
        if (string.IsNullOrEmpty(customer)) customer = await kapso.EnsureCustomer(scope.Id, tenant.Name, ct);
        await CheckCustomer(customer, ct);
        tenant.KapsoCustomerId = customer;
        // Hosted setup supports existing numbers. Do not provision/buy a new number.
        var options = new Dictionary<string, object> {
            ["language"] = "es", ["allowed_connection_types"] = new[] { "coexistence", "dedicated" },
            ["provision_phone_number"] = false, ["meta_billing_mode"] = "customer_managed"
        };
        if (Uri.TryCreate(config["FRONTEND_URL"], UriKind.Absolute, out var origin) && origin.Scheme == "https")
        {
            var app = origin.GetLeftPart(UriPartial.Authority);
            options["success_redirect_url"] = app + "/whatsapp?connection=returned";
            options["failure_redirect_url"] = app + "/whatsapp?connection=failed";
        }
        var result = Data(await kapso.Platform(HttpMethod.Post, $"customers/{Uri.EscapeDataString(customer)}/setup_links", new { setup_link = options }, ct));
        var url = Text(result, "url");
        if (!TrustedSetupUrl(url)) throw new ArgumentException("Kapso no devolvió un enlace de conexión válido");
        CrmEndpoints.Audit(db, scope, user, "channel.onboarding", scope.Id);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new SetupResult(url!, Text(result, "expires_at"));
    }
    public async Task<SyncResult> Sync(CancellationToken ct)
    {
        user.RequireAdminOrPlatform();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock(ct);
        var tenant = await db.Tenants.SingleAsync(x => x.Id == scope.Id, ct);
        var customer = tenant.KapsoCustomerId ?? config[$"Kapso:Tenants:{scope.Id}:CustomerId"];
        // On behalf, every sync leaves a row, also when it adds no number (docs/platform-owner.md AC 13).
        if (user.Platform) CrmEndpoints.Audit(db, scope, user, "channel.synced", scope.Id);
        if (string.IsNullOrEmpty(customer)) { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new SyncResult(0, 0, 0, ["Pulsa Agregar mi número para iniciar la conexión."]); }
        await CheckCustomer(customer, ct);
        var connected = new List<JsonElement>();
        for (var page = 1; ; page++)
        {
            var response = await kapso.Platform(HttpMethod.Get, $"whatsapp/phone_numbers?customer_id={Uri.EscapeDataString(customer)}&per_page=100&page={page}", ct: ct);
            var rows = Data(response).EnumerateArray().ToArray();
            connected.AddRange(rows.Where(x => IsCustomerNumber(x, customer)));
            var pages = response.TryGetProperty("meta", out var meta) && meta.TryGetProperty("total_pages", out var total) ? total.GetInt32() : (rows.Length == 100 ? page + 1 : page);
            if (page >= pages) break;
            if (page >= 50) throw new ArgumentException("Demasiados números para sincronizar; contacta al administrador de plataforma");
        }
        var added = 0; var hooks = 0; var warnings = new List<string>();
        foreach (var number in connected.DistinctBy(x => Text(x, "phone_number_id")))
        {
            var id = Text(number, "phone_number_id")!;
            var channel = await db.Channels.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.PhoneNumberId == id, ct);
            if (channel is not null && channel.TenantId != scope.Id) throw new AccessDeniedException();
            if (channel is null)
            {
                var display = Text(number, "display_phone_number") ?? Text(number, "display_name") ?? "Número conectado";
                channel = new Channel { TenantId = scope.Id, PhoneNumberId = id, Name = "WhatsApp · " + display[..Math.Min(display.Length, 120)], Enabled = false, KapsoCustomerId = customer, Coexistence = Bool(number, "is_coexistence") };
                db.Channels.Add(channel); added++;
                CrmEndpoints.Audit(db, scope, user, "channel.connected", channel.Id);
            }
            else { channel.Coexistence = Bool(number, "is_coexistence"); channel.KapsoCustomerId = customer; }
        }
        // Commit ownership before enabling delivery: an immediate provider event must
        // be able to find this channel from a different database connection.
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await using var webhookTx = await db.Database.BeginTransactionAsync(ct);
        await Lock(ct); // Serialize webhook reconciliation across API replicas too.
        foreach (var number in connected.DistinctBy(x => Text(x, "phone_number_id")))
        {
            var id = Text(number, "phone_number_id")!;
            try { if (await EnsureWebhook(id, ct)) hooks++; else warnings.Add("Número guardado. La plataforma debe configurar la URL HTTPS de recepción y su firma."); }
            catch (HttpRequestException) { warnings.Add("Número guardado. Kapso no pudo configurar la recepción; vuelve a pulsar Verificar conexión."); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { warnings.Add("Número guardado. La configuración de recepción tardó demasiado; vuelve a verificar."); }
        }
        await webhookTx.CommitAsync(ct);
        return new SyncResult(connected.Count, added, hooks, warnings.Distinct().ToArray());
    }
    async Task Lock(CancellationToken ct) => await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(scope.Id.ToByteArray(), 0)})", ct);
    async Task CheckCustomer(string customer, CancellationToken ct)
    {
        if (!Guid.TryParse(customer, out _) || await db.Tenants.AnyAsync(x => x.Id != scope.Id && x.KapsoCustomerId == customer, ct)) throw new AccessDeniedException();
    }
    async Task<bool> EnsureWebhook(string id, CancellationToken ct)
    {
        var secret = config["KAPSO_WEBHOOK_SECRET"];
        if (!Uri.TryCreate(config["KAPSO_WEBHOOK_URL"], UriKind.Absolute, out var url) || url.Scheme != "https" || string.IsNullOrEmpty(secret)) return false;
        var path = $"whatsapp/phone_numbers/{id}/webhooks";
        JsonElement? found = null;
        for (var page = 1; ; page++)
        {
            var response = await kapso.Platform(HttpMethod.Get, path + $"?per_page=100&page={page}", ct: ct);
            var rows = Data(response).EnumerateArray().ToArray();
            found = rows.Where(x => Text(x, "url") == url.AbsoluteUri && Text(x, "kind") == "kapso").Select(x => (JsonElement?)x).FirstOrDefault();
            if (found.HasValue || rows.Length < 100) break;
            if (page >= 50) throw new ArgumentException("Revisa los webhooks existentes de este número");
        }
        if (found is { } existing && Bool(existing, "active") && Text(existing, "secret_key") == secret && Text(existing, "payload_version") == "v2" && existing.TryGetProperty("events", out var events) && Events.All(e => events.EnumerateArray().Any(x => x.GetString() == e))) return true;
        var webhook = new { url = url.AbsoluteUri, secret_key = secret, kind = "kapso", active = true, payload_version = "v2", events = Events };
        await kapso.Platform(found.HasValue ? HttpMethod.Patch : HttpMethod.Post, found.HasValue ? path + "/" + Uri.EscapeDataString(Text(found.Value, "id")!) : path, new { whatsapp_webhook = webhook }, ct);
        return true;
    }
    public static bool TrustedSetupUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == "https" && (url.Host == "setup.kapso.ai" || url.Host == "app.kapso.ai") && string.IsNullOrEmpty(url.UserInfo) && url.IsDefaultPort;
    public static bool IsCustomerNumber(JsonElement number, string customer) => Text(number, "customer_id") == customer && Text(number, "status") == "CONNECTED" && !string.Equals(Text(number, "kind"), "sandbox", StringComparison.OrdinalIgnoreCase) && Text(number, "phone_number_id") is { Length: > 0 and <= 50 } id && id.All(char.IsAsciiDigit);
    static JsonElement Data(JsonElement value) => value.TryGetProperty("data", out var data) ? data : value;
    static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    static bool Bool(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.True;
}
public record SetupResult(string Url, string? ExpiresAt);
public record SyncResult(int Connected, int Added, int WebhooksReady, string[] Warnings);
