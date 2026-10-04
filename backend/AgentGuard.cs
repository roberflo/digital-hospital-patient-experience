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
    /// <param name="registering">The agent's previous message asked for the emergency contact, so «Emergencia: …» labels that field.</param>
    public static string? Inbound(string body, string type, bool registering = false) =>
        NamesEmergency(body, registering) || Person().IsMatch(body) ? "El paciente solicita atención personal o requiere valoración prioritaria."
        : type != "text" && body == "[Archivo recibido]" ? "Archivo recibido; requiere revisión humana." : null;

    /// <summary>The message names an emergency. The word is read in context: «contacto de emergencia» is a registration
    /// field the agent itself asks for, and «no es una emergencia» answers the agent's own question.</summary>
    public static bool NamesEmergency(string body, bool registering = false) =>
        Emergency().IsMatch(NotAnEmergency().Replace(EmergencyContact().Replace(registering ? EmergencyLabel().Replace(body, "") : body, ""), ""));

    /// <summary>Only a greeting: answered at once with the menu, without the model.</summary>
    public static bool IsGreeting(string body) => Greeting().IsMatch(body);

    /// <summary>A slot the patient tapped in the list of free hours: start, doctor, minutes and the doctor's name as shown.</summary>
    public static (string StartsAt, Guid Doctor, int Minutes, string Name)? SlotChoice(string body) =>
        Slot().Match(body.Trim()) is { Success: true } m && Guid.TryParse(m.Groups[2].Value, out var doctor) ? (m.Groups[1].Value, doctor, int.Parse(m.Groups[3].Value), Unsafe().Replace(m.Groups[4].Value, "").Trim()) : null;

    /// <summary>The patient wants a prescription that does not exist yet. Re-sending the previous one would be a refill nobody authorised.</summary>
    public static bool AsksNewPrescription(string body) => Refill().IsMatch(body);

    /// <summary>A plain «sí» to the emergency question is an emergency; the model is not asked to judge it.</summary>
    public static bool Affirms(string body) => Yes().IsMatch(body);

    /// <summary>The reply tells the patient they are being transferred. The runtime then transfers them for real.</summary>
    public static bool ClaimsHandoff(string reply) => Transfer().IsMatch(reply);

    /// <summary>Checks a model reply before it reaches the patient. <paramref name="grounding"/> is
    /// what the agent may repeat: the guide, this turn's tool results and earlier staff messages.
    /// Returns the violated category, or null when the reply may be sent.</summary>
    public static string? Outbound(string reply, string grounding, bool proposed, string instructions = "", string patient = "")
    {
        // Any whole instruction line recited back is a leak, whatever the patient asked for.
        // ponytail: verbatim lines only; a paraphrased leak is left to the prompt and the evals.
        if (instructions.Split('\n').Select(line => line.Trim()).Any(line => line.Length >= 40 && reply.Contains(line, StringComparison.OrdinalIgnoreCase))) return "instrucciones";
        if (reply.Contains(GuideOpen, StringComparison.OrdinalIgnoreCase) || reply.Contains(GuideClose, StringComparison.OrdinalIgnoreCase)) return "instrucciones";
        if (Link().Matches(reply).Any(m => !grounding.Contains(m.Value.TrimEnd('.', ',', ')', ';'), StringComparison.OrdinalIgnoreCase))) return "enlace";
        var known = Compact(grounding);
        // ponytail: numeric doses only; one written in words («dos tabletas») is left to the prompt and the evals.
        if (Dose().Matches(reply).Any(m => !known.Contains(Compact(m.Value)))) return "dosis";
        // Same rule for clock times: an hour the agenda, the guide or the staff never gave is an invented slot.
        // ponytail: HH:mm only; «9 am» or «a las nueve» are left to the prompt and the evals.
        // An hour the patient asked for may be echoed back («no hay a las 15:00»); a dose the patient mentioned may not.
        var hours = Time().Matches(grounding + "\n" + patient).Select(Clock).ToHashSet();
        if (Time().Matches(reply).Any(m => !hours.Contains(Clock(m)))) return "horario";
        return proposed && Done().IsMatch(reply) ? "confirmación" : null;
    }
    static string Clock(Match time) => int.Parse(time.Groups[1].Value) + ":" + time.Groups[2].Value;
    static string Compact(string text) => Spaces().Replace(text.ToLowerInvariant(), "");

    [GeneratedRegex(@"(?i)\b(hablar|comunicarme|comunicar|contactar)\b.{0,60}\b(doctor|doctora|médico|medico|humano|persona)\b")] private static partial Regex Person();
    [GeneratedRegex(@"(?i)\b(emergencia|sobredosis|suicidio)\b")] private static partial Regex Emergency();
    [GeneratedRegex(@"(?i)\b(?:otra|nueva)\s+receta\b|\bresurt\w*|\bse\s+me\s+(?:acab[oó]|termin[oó])\b|\brec[eé]t[ae]me\b|\brenovar\s+(?:la|mi)\s+receta\b")] private static partial Regex Refill();
    [GeneratedRegex(@"(?i)contactos?\s+de\s+emergencias?")] private static partial Regex EmergencyContact();
    [GeneratedRegex(@"(?i)\bemergencias?\s*:")] private static partial Regex EmergencyLabel();
    [GeneratedRegex(@"(?i)\bno\s+(?:es|se\s+trata\s+de|tengo|hay)\s+(?:una\s+|ninguna\s+)?emergencias?\b|\bninguna\s+emergencia\b|\bsin\s+emergencia\b")] private static partial Regex NotAnEmergency();
    [GeneratedRegex(@"(?i)\b(?:voy|vamos|procedo)\s+a\s+(?:derivar|transferir|pasar|comunicar)\w*|\b(?:te|lo|la|le)\s+(?:derivo|transfiero|paso|comunico|derivar[eé]|transferir[eé]|pasar[eé]|comunicar[eé])\b|\b(?:un|una)\s+(?:agente|asesor|asesora|persona|miembro del equipo)\s+(?:te|lo|la|le)\s+atender[aá]")] private static partial Regex Transfer();
    [GeneratedRegex(@"(?i)(?:https?://|www\.)\S+")] private static partial Regex Link();
    [GeneratedRegex(@"(?i)\b\d+(?:[.,]\d+)?\s*(?:mg|mcg|µg|g|ml|ui|gotas?|tabletas?|pastillas?|c[aá]psulas?|comprimidos?|ampollas?|cucharadas?)\b|\bcada\s+\d+\s*(?:horas?|hrs?|h|d[ií]as?)\b")] private static partial Regex Dose();
    [GeneratedRegex(@"(?i)\b(?:qued[oó]|est[aá]|ha\s+sido|ha\s+quedado|fue)\s+(?:ya\s+)?(?:confirmad|agendad|reprogramad|cancelad|reservad|programad)[ao]\b|\b(?:he|hemos)\s+(?:confirmado|agendado|reprogramado|cancelado|reservado)\b|\b(?:cancel|agend|reprogram|reserv)é\b")] private static partial Regex Done();
    [GeneratedRegex(@"(?i)^\W*(s[ií]|sip|claro|correcto|afirmativo|as[ií] es)\b")] private static partial Regex Yes();
    [GeneratedRegex(@"\b([01]?\d|2[0-3]):([0-5]\d)\b")] private static partial Regex Time();
    [GeneratedRegex(@"(?i)^\W*(?:hola|holi|buenas|buen\s+d[ií]a|buenos\s+d[ií]as|buenas\s+tardes|buenas\s+noches|saludos|hi|hello)(?:\W+(?:buenas|buen\s+d[ií]a|buenos\s+d[ií]as|buenas\s+tardes|buenas\s+noches))?\W*$")] private static partial Regex Greeting();
    [GeneratedRegex(@"^CITA (\S{1,40}) ([0-9a-fA-F-]{36}) (\d{1,3})(?: (.{1,60}))?$")] private static partial Regex Slot();
    [GeneratedRegex(@"[^\p{L}\p{M} .'-]")] private static partial Regex Unsafe();
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
