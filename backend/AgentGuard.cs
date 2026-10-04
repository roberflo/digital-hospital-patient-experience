using System.ClientModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
namespace Recepcion;

/// <summary>Rules that decide without asking the model (docs/reception-agent.md). The prompt asks
/// the model to stay inside reception work; these make the dangerous misses impossible to send.</summary>
public static partial class AgentGuard
{
    public const int ToolBudget = 12;
    /// <summary>With no free slot inside this window the agent asks whether it is an emergency.</summary>
    public const int UrgentWindowHours = 8;
    public const string EmergencyQuestion = "¿Es una emergencia?";
    public const string GuideOpen = "GUÍA DE ATENCIÓN (datos):", GuideClose = "FIN GUÍA.";

    /// <summary>Messages a person must answer; the model is never called. Returns the handoff reason.</summary>
    public static string? Inbound(string body, string type) =>
        // «Contacto de emergencia» is a registration field the agent itself asks for, not an emergency.
        Urgent().IsMatch(EmergencyContact().Replace(body, "")) ? "El paciente solicita atención personal o requiere valoración prioritaria."
        : type != "text" && body == "[Archivo recibido]" ? "Archivo recibido; requiere revisión humana." : null;

    /// <summary>A plain «sí» to the emergency question is an emergency; the model is not asked to judge it.</summary>
    public static bool Affirms(string body) => Yes().IsMatch(body);

    /// <summary>Checks a model reply before it reaches the patient. <paramref name="grounding"/> is
    /// what the agent may repeat: the guide, this turn's tool results and earlier staff messages.
    /// Returns the violated category, or null when the reply may be sent.</summary>
    public static string? Outbound(string reply, string grounding, bool proposed)
    {
        if (reply.Contains(GuideOpen, StringComparison.OrdinalIgnoreCase) || reply.Contains(GuideClose, StringComparison.OrdinalIgnoreCase)) return "instrucciones";
        if (Link().Matches(reply).Any(m => !grounding.Contains(m.Value.TrimEnd('.', ',', ')', ';'), StringComparison.OrdinalIgnoreCase))) return "enlace";
        var known = Compact(grounding);
        // ponytail: numeric doses only; one written in words («dos tabletas») is left to the prompt and the evals.
        if (Dose().Matches(reply).Any(m => !known.Contains(Compact(m.Value)))) return "dosis";
        return proposed && Done().IsMatch(reply) ? "confirmación" : null;
    }
    static string Compact(string text) => Spaces().Replace(text.ToLowerInvariant(), "");

    [GeneratedRegex(@"(?i)\b(hablar|comunicarme|comunicar|contactar)\b.{0,60}\b(doctor|doctora|médico|medico|humano|persona)\b|\b(emergencia|sobredosis|suicidio)\b")] private static partial Regex Urgent();
    [GeneratedRegex(@"(?i)contactos?\s+de\s+emergencias?")] private static partial Regex EmergencyContact();
    [GeneratedRegex(@"(?i)(?:https?://|www\.)\S+")] private static partial Regex Link();
    [GeneratedRegex(@"(?i)\b\d+(?:[.,]\d+)?\s*(?:mg|mcg|µg|g|ml|ui|gotas?|tabletas?|pastillas?|c[aá]psulas?|comprimidos?|ampollas?|cucharadas?)\b|\bcada\s+\d+\s*(?:horas?|hrs?|h|d[ií]as?)\b")] private static partial Regex Dose();
    [GeneratedRegex(@"(?i)\b(?:qued[oó]|est[aá]|ha\s+sido|ha\s+quedado|fue)\s+(?:ya\s+)?(?:confirmad|agendad|reprogramad|cancelad|reservad|programad)[ao]\b|\b(?:he|hemos)\s+(?:confirmado|agendado|reprogramado|cancelado|reservado)\b|\b(?:cancel|agend|reprogram|reserv)é\b")] private static partial Regex Done();
    [GeneratedRegex(@"(?i)^\W*(s[ií]|sip|claro|correcto|afirmativo|as[ií] es)\b")] private static partial Regex Yes();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}

/// <summary>Repeats one failed model call on the next provider. Only the model request is
/// repeated: tools run between calls, in this process, and are never replayed.</summary>
public sealed class FallbackChatClient(IChatClient primary, IChatClient fallback, Action onFallback) : DelegatingChatClient(primary)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sent = messages.ToList();
        try { return await base.GetResponseAsync(sent, options, cancellationToken); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or ClientResultException { Status: 0 or 408 or 429 or >= 500 })
        {
            onFallback(); return await fallback.GetResponseAsync(sent, options, cancellationToken);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) fallback.Dispose(); base.Dispose(disposing); }
}
