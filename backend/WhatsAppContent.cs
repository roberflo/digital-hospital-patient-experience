using System.Text.Json;
using System.Text.RegularExpressions;
namespace Recepcion;

/// <summary>What a received WhatsApp message says, as the conversation stores it.</summary>
public static partial class WhatsAppContent
{
    public static string Inbound(JsonElement message, JsonElement kapso)
    {
        // A tapped button or list row. The ids this product issues are the same commands a patient could type
        // (CONFIRMAR …, CITA …), so they act exactly as if typed; any other choice reads as its visible label.
        if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("interactive", out var interactive) && interactive.ValueKind == JsonValueKind.Object)
            foreach (var kind in (string[])["button_reply", "list_reply"])
                if (interactive.TryGetProperty(kind, out var reply))
                    return Text(reply, "id")?.Trim() is { } id && Command().IsMatch(id) ? id : Text(reply, "title") ?? "[Archivo recibido]";
        return Text(kapso, "content") ?? (message.TryGetProperty("text", out var text) ? Text(text, "body") : null) ?? "[Archivo recibido]";
    }
    static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.String ? found.GetString() : null;
    [GeneratedRegex(@"^(?:CONFIRMAR [0-9A-F]{6}|CORREGIR [0-9A-F]{6}|EMERGENCIA|ACTIVAR RECORDATORIOS|BAJA|AGENDAR|RECETA|PERSONA|MENU|MISCITAS|(?:VERCITA|CANCELAR) [0-9a-fA-F-]{36}|MOVER [0-9a-fA-F-]{36}(?: \S{10,40} [0-9a-fA-F-]{36} \d{1,3}(?: .{1,60})?)?|CITA \S{10,40} [0-9a-fA-F-]{36} \d{1,3}(?: .{1,60})?|VER \d{4}-\d{2}-\d{2} (?:[0-9a-fA-F-]{36}|[*+?]) [MT?])$")] private static partial Regex Command();
}

/// <summary>Options the patient can tap instead of typing. Up to three short ones are reply buttons; more, or any with a description, are a list.</summary>
public sealed record Choices(IReadOnlyList<Choice> Options, string Button = "Ver opciones")
{
    /// <summary>WhatsApp caps an interactive body at 1024 characters; longer text goes out plain.</summary>
    public const int MaxBody = 1024;
    public object ToWhatsApp(string body) => Options.Count <= 3 && Options.All(option => option.Description is null)
        ? new { type = "button", body = new { text = body }, action = new { buttons = Options.Select(option => new { type = "reply", reply = new { id = option.Id, title = Cut(option.Title, 20) } }) } }
        : new { type = "list", body = new { text = body }, action = new { button = Cut(Button, 20), sections = new[] { new { title = "Opciones", rows = Options.Take(10).Select(option => new Dictionary<string, string> { ["id"] = option.Id, ["title"] = Cut(option.Title, 24), ["description"] = Cut(option.Description ?? "", 72) }) } } } };
    static string Cut(string text, int max) => text.Length > max ? text[..max] : text;
}
public sealed record Choice(string Id, string Title, string? Description = null);
