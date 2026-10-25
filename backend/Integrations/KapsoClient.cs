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
    public async Task<JsonElement> Platform(HttpMethod method, string path, object? body = null, CancellationToken ct = default, bool general = false)
    {
        using var req = new HttpRequestMessage(method, "https://api.kapso.ai/platform/v1/" + path);
        req.Headers.Add("X-API-Key", general ? GeneralKey() : config["KAPSO_API_KEY"] ?? throw new ArgumentException("Kapso no configurado"));
        if (body is not null) req.Content = JsonContent.Create(body);
        using var res = await http.SendAsync(req, ct); res.EnsureSuccessStatusCode(); return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
    public async Task<string> Send(string number, string phone, string body, string? mediaId = null, string type = "text", CancellationToken ct = default, bool manual = false, object? interactive = null)
    {
        RequireSending(manual);
        if (interactive is not null) type = "interactive";
        var payload = new Dictionary<string, object> { { "messaging_product", "whatsapp" }, { "to", Rules.WhatsAppTo(phone) }, { "type", type } };
        payload[type] = interactive ?? (type == "text" ? new { body } : new Dictionary<string, object> { { "id", mediaId ?? throw new ArgumentException("Archivo requerido") } });
        using var req = Request(HttpMethod.Post, $"{number}/messages"); req.Content = JsonContent.Create(payload);
        using var res = await http.SendAsync(req, ct); res.EnsureSuccessStatusCode();
        try
        {
            var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("messages", out var sent) && sent.ValueKind == JsonValueKind.Array && sent.GetArrayLength() > 0
                && sent[0].ValueKind == JsonValueKind.Object && sent[0].TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } value) return value;
        }
        catch (JsonException) { }
        // Accepted but unreadable: it may have gone out, so the caller treats it like a 5xx (uncertain, never retried).
        throw new HttpRequestException("Kapso aceptó el envío sin devolver el id del mensaje");
    }
    public async Task<JsonElement> ReminderTemplate(string number, string name, string language, CancellationToken ct = default, bool general = false)
    {
        var raw = await Platform(HttpMethod.Get, "whatsapp/phone_numbers/" + Uri.EscapeDataString(number), ct: ct, general: general);
        var data = raw.TryGetProperty("data", out var d) ? d : raw;
        var waba = data.GetProperty("business_account_id").GetString()!;
        using var req = Request(HttpMethod.Get, general, Uri.EscapeDataString(waba) + "/message_templates?name=" + Uri.EscapeDataString(name) + "&limit=100");
        using var res = await http.SendAsync(req, ct); res.EnsureSuccessStatusCode();
        var payload = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return payload.GetProperty("data").EnumerateArray().FirstOrDefault(t => t.GetProperty("name").GetString() == name && t.GetProperty("language").GetString() == language);
    }
    static readonly string[] DayButtons=["Confirmar","Cambiar fecha","Cancelar"];
    /// <summary>The general number's windows (S3): its own templates, with the clinic's phone; the day before has one «Cancelar cita» (A13, A-P1).</summary>
    public const string GeneralDayWindow="general_day_before", GeneralHourWindow="general_hour_before";
    public const string GeneralDayTemplate="recepcion_cita_general_dia_anterior_v1", GeneralHourTemplate="recepcion_cita_general_una_hora_v1";
    /// <summary>The shape a reminder template must have for its window: the hour before is one BODY; the day before is a BODY,
    /// an optional FOOTER and exactly the three quick replies, in that order. The BAJA instruction is in the BODY or the FOOTER.
    /// The general number's windows also name {{telefono}}, may carry a FOOTER, and its day before has exactly one «Cancelar cita».
    /// Anything else (a header, a URL or phone button, another parameter) is refused.</summary>
    public static bool ValidReminderTemplate(JsonElement template, string window)
    {
        if (template.ValueKind != JsonValueKind.Object || !template.TryGetProperty("status",out var status) || status.GetString() != "APPROVED"
            || !template.TryGetProperty("category",out var category) || category.GetString() != "UTILITY"
            || !template.TryGetProperty("parameter_format",out var format) || format.GetString() != "NAMED"
            || !template.TryGetProperty("components",out var components) || components.ValueKind != JsonValueKind.Array) return false;
        var types=components.EnumerateArray().Select(c=>c.GetProperty("type").GetString()).ToArray();
        var general=window is GeneralDayWindow or GeneralHourWindow;
        var shapeOk=window switch{
            "hour_before"=>types.SequenceEqual(["BODY"]),
            GeneralHourWindow=>types.SequenceEqual(["BODY"])||types.SequenceEqual(["BODY","FOOTER"]),
            "day_before" or GeneralDayWindow=>types.SequenceEqual(["BODY","BUTTONS"])||types.SequenceEqual(["BODY","FOOTER","BUTTONS"]),
            _=>false};
        if(!shapeOk)return false;
        var text=components[0].GetProperty("text").GetString()??"";
        var parameters=System.Text.RegularExpressions.Regex.Matches(text,@"\{\{([a-z_]+)\}\}").Select(m=>m.Groups[1].Value).Order().ToArray();
        var footer=types.Length>1&&types[1]=="FOOTER"?components[1].GetProperty("text").GetString()??"":"";
        if(!parameters.SequenceEqual(general?new[]{"fecha","hora","hospital","telefono"}:new[]{"fecha","hora","hospital"})||!(text+"\n"+footer).Contains("BAJA",StringComparison.Ordinal))return false;
        if(window is "hour_before" or GeneralHourWindow)return true;
        var buttons=components[types.Length-1];
        return buttons.TryGetProperty("buttons",out var list)&&list.ValueKind==JsonValueKind.Array
            &&list.EnumerateArray().Select(b=>b.GetProperty("type").GetString()=="QUICK_REPLY"?b.GetProperty("text").GetString():null).SequenceEqual(general?["Cancelar cita"]:DayButtons);
    }
    public async Task<bool> ReminderTemplateApproved(string number,string name,string language,string window,CancellationToken ct=default,bool general=false) => ValidReminderTemplate(await ReminderTemplate(number,name,language,ct,general),window);
    /// <summary>Sends an approved reminder. <paramref name="buttons"/> are the quick-reply payloads, in the template's button order; the caller composes them.
    /// <paramref name="general"/>: from the platform's account (criterio 39), never a clinic's key; <paramref name="phoneOfClinic"/> fills {{telefono}}.
    /// Returns the message id and, when it came back, <c>contacts[0].wa_id</c> (TEL-1 criterios 21–22, measured 2026-10-25).</summary>
    public async Task<(string Id, string? WaId)> SendReminder(string number,string phone,string name,string language,string hospital,string date,string time,CancellationToken ct=default,IReadOnlyList<string>? buttons=null,bool general=false,string? phoneOfClinic=null)
    {
        if(config["REMINDERS_SEND_ENABLED"]!="true")throw new ArgumentException("Recordatorios en pausa");
        using var req=Request(HttpMethod.Post,general,$"{number}/messages");
        var values=new List<object>{new{type="text",parameter_name="hospital",text=hospital},new{type="text",parameter_name="fecha",text=date},new{type="text",parameter_name="hora",text=time}};
        if(phoneOfClinic is not null)values.Add(new{type="text",parameter_name="telefono",text=phoneOfClinic});
        var components=new List<object>{new{type="body",parameters=values}};
        components.AddRange((buttons??[]).Select((payload,i)=>new{type="button",sub_type="quick_reply",index=i.ToString(System.Globalization.CultureInfo.InvariantCulture),parameters=new[]{new{type="payload",payload}}}));
        req.Content=JsonContent.Create(new{messaging_product="whatsapp",to=Rules.WhatsAppTo(phone),type="template",template=new{name,language=new{code=language},components}});
        using var res=await http.SendAsync(req,ct);res.EnsureSuccessStatusCode();
        // A 2xx without a message id may still have been delivered: uncertain, like a 5xx, never retried.
        JsonElement sent;
        try { sent=await res.Content.ReadFromJsonAsync<JsonElement>(ct); } catch(JsonException) { throw new HttpRequestException("Kapso answered without a message id"); }
        var messageId=sent.ValueKind==JsonValueKind.Object&&sent.TryGetProperty("messages",out var messages)&&messages.ValueKind==JsonValueKind.Array&&messages.GetArrayLength()>0
            &&messages[0].ValueKind==JsonValueKind.Object&&messages[0].TryGetProperty("id",out var id)&&id.ValueKind==JsonValueKind.String&&id.GetString() is {Length:>0} value
            ?value:throw new HttpRequestException("Kapso answered without a message id");
        var waId=sent.TryGetProperty("contacts",out var contacts)&&contacts.ValueKind==JsonValueKind.Array&&contacts.GetArrayLength()>0&&contacts[0].ValueKind==JsonValueKind.Object
            &&contacts[0].TryGetProperty("wa_id",out var wa)&&wa.ValueKind==JsonValueKind.String&&wa.GetString() is {Length:>0} w?w:null;
        return (messageId,waId);
    }
    /// <summary>The general number's fixed replies (criterios 22–25): from the platform's number and key, gated like the reminders themselves.</summary>
    public async Task SendGeneral(string phone, string? body, object? interactive = null, CancellationToken ct = default)
    {
        if (config["REMINDERS_SEND_ENABLED"] != "true") throw new ArgumentException("Recordatorios en pausa");
        var payload = new Dictionary<string, object> { { "messaging_product", "whatsapp" }, { "to", Rules.WhatsAppTo(phone) }, { "type", interactive is null ? "text" : "interactive" } };
        payload[interactive is null ? "text" : "interactive"] = interactive ?? new { body };
        using var req = Request(HttpMethod.Post, true, $"{GeneralNumber()}/messages"); req.Content = JsonContent.Create(payload);
        using var res = await http.SendAsync(req, ct); res.EnsureSuccessStatusCode();
    }
    /// <summary>The platform's general number (criterio 39). Missing ⇒ «Cuenta general no configurada»; never a clinic's (criterio 40).</summary>
    public string GeneralNumber() => config["KAPSO_PLATFORM_PHONE_NUMBER_ID"] is { Length: > 0 } number ? number : throw new ArgumentException("Cuenta general no configurada");
    public string GeneralKey() => config["KAPSO_PLATFORM_API_KEY"] is { Length: > 0 } key ? key : throw new ArgumentException("Cuenta general no configurada");
    /// <summary>Marks the patient's message as read and shows «escribiendo…» until the next message is sent, or about 25 seconds.</summary>
    public async Task Typing(string number, string messageId, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, $"{number}/messages"); req.Content = JsonContent.Create(new { messaging_product = "whatsapp", status = "read", message_id = messageId, typing_indicator = new { type = "text" } });
        using var res = await http.SendAsync(req, ct); res.EnsureSuccessStatusCode();
    }
    public bool CanSend(bool manual) => config["SEND_ENABLED"] == "true" || (manual && config["KAPSO_MANUAL_SEND_ENABLED"] == "true");
    public void RequireSending(bool manual)
    {
        if (!CanSend(manual)) throw new ArgumentException(PausedReason(manual));
    }
    public static string PausedReason(bool manual) => manual ? "El envío manual está en pausa. Solicita a administración habilitarlo." : "El envío automático está desactivado.";
    public async Task<string> Upload(string number, Stream stream, string name, string contentType, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent(); content.Add(new StringContent("whatsapp"), "messaging_product");
        var file = new StreamContent(stream); file.Headers.ContentType = new MediaTypeHeaderValue(contentType); content.Add(file, "file", Path.GetFileName(name));
        using var req = Request(HttpMethod.Post, $"{number}/media"); req.Content = content;
        using var res = await http.SendAsync(req, ct); res.EnsureSuccessStatusCode(); return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetString()!;
    }
    /// <summary>The refusal of a file over WhatsApp's 20 MB, which a caller names (RXE-C: whatsapp.media_too_large).</summary>
    public const string TooLarge = "Archivo demasiado grande";
    public async Task<(byte[] Bytes, string Type)> Download(string mediaId, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, Uri.EscapeDataString(mediaId));
        using var response = await http.SendAsync(req, ct); response.EnsureSuccessStatusCode(); var meta = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var uri = new Uri(meta.GetProperty("url").GetString()!);
        if (uri.Scheme != "https" || !(uri.Host == "api.kapso.ai" || uri.Host.EndsWith(".fbsbx.com", StringComparison.Ordinal) || uri.Host.EndsWith(".fbcdn.net", StringComparison.Ordinal))) throw new ArgumentException("Origen multimedia inválido");
        using var download = new HttpRequestMessage(HttpMethod.Get, uri);
        if (uri.Host == "api.kapso.ai") download.Headers.Add("X-API-Key", config["KAPSO_API_KEY"]);
        using var media = await http.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, ct); media.EnsureSuccessStatusCode();
        if (media.Content.Headers.ContentLength > 20 * 1024 * 1024) throw new ArgumentException(TooLarge);
        await using var source = await media.Content.ReadAsStreamAsync(ct); using var target = new MemoryStream(); var buffer = new byte[65536]; int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0) { if (target.Length + read > 20 * 1024 * 1024) throw new ArgumentException(TooLarge); await target.WriteAsync(buffer.AsMemory(0, read), ct); }
        var type = meta.TryGetProperty("mime_type", out var m) ? m.GetString()! : "application/octet-stream";
        return (target.ToArray(), type);
    }
    HttpRequestMessage Request(HttpMethod method, string path) => Request(method, false, path);
    HttpRequestMessage Request(HttpMethod method, bool general, string path) { var r = new HttpRequestMessage(method, "https://api.kapso.ai/meta/whatsapp/v24.0/" + path); r.Headers.Add("X-API-Key", general ? GeneralKey() : config["KAPSO_API_KEY"]); return r; }
}
