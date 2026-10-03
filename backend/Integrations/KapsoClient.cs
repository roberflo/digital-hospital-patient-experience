using System.Net.Http.Headers;
using System.Text.Json;
namespace Recepcion.Integrations;

public sealed class KapsoClient(HttpClient http, IConfiguration config)
{
    public async Task<string> EnsureCustomer(Guid tenantId, string name, CancellationToken ct = default)
    {
        // Recover a previous successful creation if the response or local commit was lost.
        var externalId = tenantId.ToString();
        var list = await Platform(HttpMethod.Get, "customers?external_customer_id=" + externalId, null, ct);
        var matches = list.GetProperty("data").EnumerateArray().Where(x => x.GetProperty("external_customer_id").GetString() == externalId).ToArray();
        if (matches.Length > 1) throw new ArgumentException("Hay varios clientes Kapso asociados al hospital; resuelve la asociación antes de continuar");
        if (matches.Length == 1) return matches[0].GetProperty("id").GetString()!;
        var created = await Platform(HttpMethod.Post, "customers", new { customer = new { name, external_customer_id = externalId } }, ct);
        return created.GetProperty("data").GetProperty("id").GetString()!;
    }
    public async Task<JsonElement> Platform(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(method, "https://api.kapso.ai/platform/v1/" + path);
        req.Headers.Add("X-API-Key", config["KAPSO_API_KEY"] ?? throw new ArgumentException("Kapso no configurado"));
        if (body is not null) req.Content = JsonContent.Create(body);
        using var res = await http.SendAsync(req, ct); res.EnsureSuccessStatusCode(); return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
    public async Task<string> Send(string number, string phone, string body, string? mediaId = null, string type = "text", CancellationToken ct = default, bool manual = false)
    {
        RequireSending(manual);
        var payload = new Dictionary<string, object> { { "messaging_product", "whatsapp" }, { "to", Rules.Phone(phone) }, { "type", type } };
        payload[type] = type == "text" ? new { body } : new Dictionary<string, object> { { "id", mediaId ?? throw new ArgumentException("Archivo requerido") } };
        using var req = Request(HttpMethod.Post, $"{number}/messages"); req.Content = JsonContent.Create(payload);
        using var res = await http.SendAsync(req, ct); res.EnsureSuccessStatusCode(); var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.GetProperty("messages")[0].GetProperty("id").GetString()!;
    }
    public async Task<JsonElement> ReminderTemplate(string number, string name, string language, CancellationToken ct = default)
    {
        var raw = await Platform(HttpMethod.Get, "whatsapp/phone_numbers/" + Uri.EscapeDataString(number), ct: ct);
        var data = raw.TryGetProperty("data", out var d) ? d : raw;
        var waba = data.GetProperty("business_account_id").GetString()!;
        using var req = Request(HttpMethod.Get, Uri.EscapeDataString(waba) + "/message_templates?name=" + Uri.EscapeDataString(name) + "&limit=100");
        using var res = await http.SendAsync(req, ct); res.EnsureSuccessStatusCode();
        var payload = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return payload.GetProperty("data").EnumerateArray().FirstOrDefault(t => t.GetProperty("name").GetString() == name && t.GetProperty("language").GetString() == language);
    }
    public static bool ValidReminderTemplate(JsonElement template)
    {
        if (template.ValueKind != JsonValueKind.Object || !template.TryGetProperty("status",out var status) || status.GetString() != "APPROVED"
            || !template.TryGetProperty("category",out var category) || category.GetString() != "UTILITY"
            || !template.TryGetProperty("parameter_format",out var format) || format.GetString() != "NAMED") return false;
        if(!template.TryGetProperty("components",out var components)||components.GetArrayLength()!=1)return false;
        var body=components[0];if(body.GetProperty("type").GetString()!="BODY")return false;
        var text=body.GetProperty("text").GetString()??"";
        var parameters=System.Text.RegularExpressions.Regex.Matches(text,@"\{\{([a-z_]+)\}\}").Select(m=>m.Groups[1].Value).Order().ToArray();
        return parameters.SequenceEqual(new[]{"fecha","hora","hospital"})&&text.Contains("BAJA",StringComparison.Ordinal);
    }
    public async Task<bool> ReminderTemplateApproved(string number,string name,string language,CancellationToken ct=default) => ValidReminderTemplate(await ReminderTemplate(number,name,language,ct));
    public async Task<string> SendReminder(string number,string phone,string name,string language,string hospital,string date,string time,CancellationToken ct=default)
    {
        if(config["REMINDERS_SEND_ENABLED"]!="true")throw new ArgumentException("Recordatorios en pausa");
        using var req=Request(HttpMethod.Post,$"{number}/messages");
        req.Content=JsonContent.Create(new{messaging_product="whatsapp",to=Rules.Phone(phone),type="template",template=new{name,language=new{code=language},components=new[]{new{type="body",parameters=new[]{new{type="text",parameter_name="hospital",text=hospital},new{type="text",parameter_name="fecha",text=date},new{type="text",parameter_name="hora",text=time}}}}}});
        using var res=await http.SendAsync(req,ct);res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("messages")[0].GetProperty("id").GetString()!;
    }
    public bool CanSend(bool manual) => config["SEND_ENABLED"] == "true" || (manual && config["KAPSO_MANUAL_SEND_ENABLED"] == "true");
    public void RequireSending(bool manual)
    {
        if (!CanSend(manual)) throw new ArgumentException(manual ? "El envío manual está en pausa. Solicita a administración habilitarlo." : "El envío automático está desactivado.");
    }
    public async Task<string> Upload(string number, Stream stream, string name, string contentType, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent(); content.Add(new StringContent("whatsapp"), "messaging_product");
        var file = new StreamContent(stream); file.Headers.ContentType = new MediaTypeHeaderValue(contentType); content.Add(file, "file", Path.GetFileName(name));
        using var req = Request(HttpMethod.Post, $"{number}/media"); req.Content = content;
        using var res = await http.SendAsync(req, ct); res.EnsureSuccessStatusCode(); return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetString()!;
    }
    public async Task<(byte[] Bytes, string Type)> Download(string mediaId, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, Uri.EscapeDataString(mediaId));
        using var response = await http.SendAsync(req, ct); response.EnsureSuccessStatusCode(); var meta = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var uri = new Uri(meta.GetProperty("url").GetString()!);
        if (uri.Scheme != "https" || !(uri.Host == "api.kapso.ai" || uri.Host.EndsWith(".fbsbx.com", StringComparison.Ordinal) || uri.Host.EndsWith(".fbcdn.net", StringComparison.Ordinal))) throw new ArgumentException("Origen multimedia inválido");
        using var download = new HttpRequestMessage(HttpMethod.Get, uri);
        if (uri.Host == "api.kapso.ai") download.Headers.Add("X-API-Key", config["KAPSO_API_KEY"]);
        using var media = await http.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, ct); media.EnsureSuccessStatusCode();
        if (media.Content.Headers.ContentLength > 20 * 1024 * 1024) throw new ArgumentException("Archivo demasiado grande");
        await using var source = await media.Content.ReadAsStreamAsync(ct); using var target = new MemoryStream(); var buffer = new byte[65536]; int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0) { if (target.Length + read > 20 * 1024 * 1024) throw new ArgumentException("Archivo demasiado grande"); await target.WriteAsync(buffer.AsMemory(0, read), ct); }
        var type = meta.TryGetProperty("mime_type", out var m) ? m.GetString()! : "application/octet-stream";
        return (target.ToArray(), type);
    }
    HttpRequestMessage Request(HttpMethod method, string path) { var r = new HttpRequestMessage(method, "https://api.kapso.ai/meta/whatsapp/v24.0/" + path); r.Headers.Add("X-API-Key", config["KAPSO_API_KEY"]); return r; }
}
