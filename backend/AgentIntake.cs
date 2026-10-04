using System.Text.RegularExpressions;
namespace Recepcion;

/// <summary>The registration form a new client fills in one answer at a time, with no model (docs/reception-agent.md).
/// It only reads what was written: an answer that does not fit is asked again, never completed or guessed.</summary>
public sealed partial record Intake(int Step, string? GivenNames = null, string? FamilyNames = null, string? BirthDate = null, string? Sex = null, string? EmergencyName = null, string? EmergencyRelationship = null, string? EmergencyPhone = null)
{
    public const int Steps = 7;
    public bool Complete => Step > Steps;
    /// <summary>Under 18: Hospital requires a legal guardian, which reception handles in person.</summary>
    public bool Minor => Step < 0;
    static readonly string[] Months = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    public static (Intake State, string Prompt, Choices? Choices) Start() => Ask(new Intake(1), "Con gusto. Para darte cita necesito registrarte: son 7 datos cortos.\n\n");

    /// <summary>Starts with what the patient already said, wherever it sits in the form, so it is not asked again. Each known answer goes through
    /// the same reading as a typed one: what fits is kept, what is missing or does not fit is asked, in the form's order.
    /// Nothing after the birth date is read until that date says this is an adult: a minor's data is not held.</summary>
    public static (Intake State, string Prompt, Choices? Choices) Start(DateOnly today, params string?[] known)
    {
        var state = new Intake(1);
        for (var i = 0; i < known.Length && i < Steps; i++)
        {
            if (i > 2 && state.BirthDate is null) break;
            if (string.IsNullOrWhiteSpace(known[i])) continue;
            var asked = state with { Step = i + 1 }; var read = asked.Answer(known[i]!, today).State;
            if (read.Minor) return (read, "", null);
            if (read != asked) state = read; // a rejected answer comes back as the same record: nothing of it is kept
        }
        state = state.Next(); var left = state.Data().Count(x => x is null);
        return left == Steps ? Start() : Ask(state, $"Ya tengo parte de tus datos; {(left == 1 ? "me falta uno" : $"me faltan {left}")}.\n\n");
    }
    string?[] Data() => [GivenNames, FamilyNames, BirthDate, Sex, EmergencyName, EmergencyRelationship, EmergencyPhone];
    /// <summary>The first datum still missing is the one asked next; with none missing the form is complete.</summary>
    Intake Next() => this with { Step = Array.IndexOf(Data(), null) is >= 0 and var i ? i + 1 : Steps + 1 };

    public (Intake State, string Prompt, Choices? Choices) Answer(string text, DateOnly today)
    {
        var value = text.Trim();
        switch (Step)
        {
            case 1: return Name().IsMatch(value) ? Ask((this with { GivenNames = value }).Next(), $"Gracias, {value.Split(' ')[0]}.\n\n") : Ask(this, "No pude leer tus nombres.\n\n");
            case 2: return Name().IsMatch(value) ? Ask((this with { FamilyNames = value }).Next()) : Ask(this, "No pude leer tus apellidos.\n\n");
            case 3:
                if (Date(value) is not { } born || born > today || born < today.AddYears(-120)) return Ask(this, "No pude leer esa fecha.\n\n");
                return born > today.AddYears(-18) ? (this with { Step = -1 }, "", null) : Ask((this with { BirthDate = born.ToString("yyyy-MM-dd") }).Next());
            case 4:
                var sex = value.ToLowerInvariant() switch { "femenino" or "f" or "mujer" or "female" => "female", "masculino" or "m" or "hombre" or "male" => "male", _ => null };
                return sex is null ? Ask(this, "Necesito una de las dos opciones.\n\n") : Ask((this with { Sex = sex }).Next());
            case 5: return Name().IsMatch(value) ? Ask((this with { EmergencyName = value }).Next()) : Ask(this, "No pude leer ese nombre.\n\n");
            case 6: return Name().IsMatch(value) && value.Length <= 60 ? Ask((this with { EmergencyRelationship = value }).Next()) : Ask(this, "No pude leer el parentesco.\n\n");
            case 7:
                try { return Ask((this with { EmergencyPhone = Rules.Phone(value) }).Next()); }
                catch (ArgumentException) { return Ask(this, "Ese teléfono no parece completo.\n\n"); }
            default: return (this, "", null);
        }
    }

    static (Intake State, string Prompt, Choices? Choices) Ask(Intake state, string lead = "") => state.Step switch
    {
        1 => (state, lead + "¿Cuál es tu nombre?\n" + (state.FamilyNames is null ? "Solo nombres; los apellidos te los pido enseguida." : "Solo nombres, sin apellidos."), null),
        2 => (state, lead + "¿Y tus apellidos?", null),
        3 => (state, lead + "¿Cuál es tu fecha de nacimiento?\nPor ejemplo: 12/03/1990", null),
        4 => (state, lead + "¿Qué sexo aparece en tu documento de identidad?\nEs un dato que pide el hospital para registrarte.", new Choices([new("f", "Femenino"), new("m", "Masculino")])),
        5 => (state, lead + "¿Quién es tu contacto de emergencia?\nEscribe su nombre completo.", null),
        6 => (state, lead + "¿Qué parentesco tiene contigo tu contacto de emergencia?\nPor ejemplo: madre, hermano, pareja.", null),
        7 => (state, lead + "Último dato:\n¿Cuál es el teléfono de tu contacto de emergencia?", null),
        _ => (state, "", null),
    };

    /// <summary>A birth date the way people write it here: day first («12/03/1990», «12 de marzo de 1990»), or ISO.</summary>
    static DateOnly? Date(string text)
    {
        int day, month, year;
        if (Iso().Match(text) is { Success: true } iso) (year, month, day) = (int.Parse(iso.Groups[1].Value), int.Parse(iso.Groups[2].Value), int.Parse(iso.Groups[3].Value));
        else if (Numeric().Match(text) is { Success: true } n) (day, month, year) = (int.Parse(n.Groups[1].Value), int.Parse(n.Groups[2].Value), int.Parse(n.Groups[3].Value));
        else if (Worded().Match(text) is { Success: true } w && Array.IndexOf(Months, w.Groups[2].Value.ToLowerInvariant().Replace("setiembre", "septiembre")) is >= 0 and var index) (day, month, year) = (int.Parse(w.Groups[1].Value), index + 1, int.Parse(w.Groups[3].Value));
        else return null;
        return year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;
    }

    [GeneratedRegex(@"^(?=.*\p{L}{2})[\p{L}\p{M} .'-]{2,100}$")] private static partial Regex Name();
    [GeneratedRegex(@"^\s*(\d{4})-(\d{1,2})-(\d{1,2})\s*$")] private static partial Regex Iso();
    [GeneratedRegex(@"^\s*(\d{1,2})[/\-. ](\d{1,2})[/\-. ](\d{4})\s*$")] private static partial Regex Numeric();
    [GeneratedRegex(@"(?i)^\s*(\d{1,2})\s+de\s+(\p{L}+)\s+(?:de|del)\s+(\d{4})\s*$")] private static partial Regex Worded();
}
