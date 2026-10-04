using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Recepcion;

/// <summary>What the agent knows beyond the message it is answering (docs/reception-agent.md, criterios 55–61): the conversation
/// in progress, the facts of what it did before, the preferences the patient stated, and a search of older messages on demand.
/// Nothing here calls a model, and nothing is indexed in the clear: bodies are encrypted, so they are reached by contact and filtered here.</summary>
public static partial class AgentMemory
{
    public const int SessionGapHours = 12, Window = 24, Episodes = 5, RecallScan = 300, RecallHits = 5, MaxValue = 120;
    public const string Open = "<<<MEMORIA_DEL_PACIENTE", Close = "MEMORIA_DEL_PACIENTE>>>";
    /// <summary>The only things kept about a patient between conversations. Closed and not clinical on purpose.</summary>
    public static readonly IReadOnlyDictionary<string, string> Keys = new Dictionary<string, string> { ["trato"] = "Cómo quiere que le llamen", ["doctor_preferido"] = "Doctor preferido", ["horario_preferido"] = "Horario preferido" };
    /// <summary>The agent's own trail. Staff notes are for the team and never reach the model.</summary>
    public static readonly string[] EpisodeKinds = ["appointment", "prescription_delivered", "patient_registered", "handoff", "note"];

    /// <summary>Whether a value may be kept under a key. The model decides to call the tool; what gets stored is decided here:
    /// a time of day for the schedule, a short name for the other two, and never anything about health.</summary>
    // ponytail: the health words are a short list, not a classifier. The shape rules carry the weight; widen the list if a review of stored values shows a miss.
    public static bool Accepts(string key, string value) =>
        Keys.ContainsKey(key) && value.Trim() is { Length: > 0 and <= MaxValue } text && !Health().IsMatch(text)
        && (key == "horario_preferido" ? TimeOfDay().IsMatch(text) : ShortName().IsMatch(text));

    /// <summary>The conversation in progress: what follows the last silence of <see cref="SessionGapHours"/> hours. Messages come oldest first.</summary>
    public static List<Message> Session(List<Message> history)
    {
        var start = 0;
        for (var i = 1; i < history.Count; i++) if (history[i].CreatedAt - history[i - 1].CreatedAt >= TimeSpan.FromHours(SessionGapHours)) start = i;
        return history[start..];
    }

    /// <summary>Preferences and dated facts as lines of data for the prompt; empty when there is nothing to remember.</summary>
    public static string Block(IEnumerable<ContactMemory> facts, IEnumerable<Activity> episodes, TimeZoneInfo zone)
    {
        var lines = facts.Where(f => Keys.ContainsKey(f.Key)).OrderBy(f => f.Key).Select(f => $"- {Keys[f.Key]}: {Line(f.Value, MaxValue)}")
            .Concat(episodes.OrderBy(e => e.CreatedAt).Select(e => $"- {TimeZoneInfo.ConvertTime(e.CreatedAt, zone):yyyy-MM-dd}: {(e.Kind == "handoff" ? "Pasó con una persona de recepción." : Line(Identifier().Replace(e.Body, ""), 200))}")).ToList();
        return lines.Count == 0 ? "" : string.Join("\n", lines);
    }

    /// <summary>The older messages that share the most words with the request, newest first among equals. Accents and case do not count.</summary>
    public static List<object> Search(IEnumerable<Message> older, string words, TimeZoneInfo zone)
    {
        var wanted = Fold(words).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4).Distinct().ToList();
        return older.Select(m => (Message: m, Score: wanted.Count(Fold(m.Body).Contains))).Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Message.CreatedAt).Take(RecallHits)
            .Select(x => (object)new { date = TimeZoneInfo.ConvertTime(x.Message.CreatedAt, zone).ToString("yyyy-MM-dd"), from = x.Message.Sender == "patient" ? "paciente" : "hospital", text = Line(x.Message.Body, 300) }).ToList();
    }

    /// <summary>How far a free hour is from what the patient prefers: 0 when it is their doctor at their time of day.
    /// The list is sorted by it, so nothing is hidden: what they usually want just comes first.</summary>
    public static int Distance(IReadOnlyDictionary<string, string> preferences, string doctor, DateTimeOffset local) =>
        (preferences.TryGetValue("doctor_preferido", out var who) && !Fold(who).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4 && !w.StartsWith("doctor")).Any(Fold(doctor).Contains) ? 2 : 0)
        + (preferences.TryGetValue("horario_preferido", out var when) && Band(Fold(when)) is { } band && (local.Hour < band.From || local.Hour >= band.To) ? 1 : 0);
    // ponytail: morning, afternoon, night. «Después de las 3» or «los martes» are not read; add them if patients state them that way.
    static (int From, int To)? Band(string folded) => folded.Contains("tarde") ? (12, 18) : folded.Contains("noche") ? (18, 24) : folded.Contains("temprano") ? (0, 10) : folded.Contains("manana") ? (0, 12) : null;

    static string Line(string text, int max) { var one = Regex.Replace(text, @"\s+", " ").Trim(); return one.Length > max ? one[..max] + "…" : one; }
    static string Fold(string text) => new string(text.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray());
    [GeneratedRegex(@"diab|hipertens|presi[oó]n|embaraz|al[eé]rgi|medic|pastilla|metformin|insulin|\bmg\b|dolor|enferm|diagn|c[aá]ncer|asma|tratamiento|recet|s[ií]ntoma|cirug|operaci|terapia|\bvih\b|depresi|ansiedad", RegexOptions.IgnoreCase)] private static partial Regex Health();
    [GeneratedRegex(@"mañana|tarde|noche|mediod[ií]a|temprano|lunes|martes|mi[eé]rcoles|jueves|viernes|s[aá]bado|domingo|fin de semana|\d", RegexOptions.IgnoreCase)] private static partial Regex TimeOfDay();
    [GeneratedRegex(@"^[\p{L}.'’]+( [\p{L}.'’]+){0,4}$")] private static partial Regex ShortName();
    [GeneratedRegex(@"(Referencia[^.:]*:\s*)?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\.?")] private static partial Regex Identifier();
}
