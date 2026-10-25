using System.Text.RegularExpressions;
namespace Recepcion;

/// <summary>Rules that decide without asking the model (docs/reception-agent.md). The prompt asks
/// the model to stay inside reception work; these make the dangerous misses impossible to send.</summary>
public static partial class AgentGuard
{
    public const int ToolBudget = 12;
    /// <summary>With no free slot inside this window the agent asks whether it is an emergency.</summary>
    public const int UrgentWindowHours = 8;
    public const string EmergencyQuestion = "¿Es una emergencia?";
    /// <summary>How the agent offers a person. Outside an emergency the patient decides, with a button or by answering yes.</summary>
    public const string PersonQuestion = "¿Quieres que te pase con una persona?";
    public const string GuideOpen = "GUÍA DE ATENCIÓN (datos):", GuideClose = "FIN GUÍA.";

    /// <summary>Messages a person must answer; the model is never called. Returns the handoff reason.</summary>
    /// <param name="registering">The agent's previous message asked for the emergency contact, so «Emergencia: …» labels that field.</param>
    /// <param name="urgent">The turn already names an emergency, read across the patient's messages (AgentRuntime).</param>
    public static string? Inbound(string body, string type, bool registering = false, bool urgent = false) =>
        urgent || NamesEmergency(body, registering) || Person().IsMatch(body) ? "El paciente solicita atención personal o requiere valoración prioritaria."
        : type != "text" && body == "[Archivo recibido]" ? "Archivo recibido; requiere revisión humana." : null;

    /// <summary>The message names an emergency. The word is read in context: «contacto de emergencia» is a registration
    /// field the agent itself asks for, and «no es una emergencia» answers the agent's own question.</summary>
    public static bool NamesEmergency(string body, bool registering = false) =>
        Emergency().IsMatch(NotAnEmergency().Replace(EmergencyContact().Replace(registering ? EmergencyLabel().Replace(body, "") : body, ""), "")) || AlarmSign(body);

    /// <summary>MR-35: the alarm signs the owner approved on 2026-10-17 (docs/reception-agent.md, «Señales de alarma»), one pattern per sign.
    /// Read without accents or case. A sign fires unless it is negated, or passed or resolved and not said to be still going.
    /// Suicidal words and self-harm fire always.</summary>
    static bool AlarmSign(string body)
    {
        // ponytail: the last 4096 characters of a message; the scan restarts per match, so a longer one would cost quadratic time.
        // Signs are searched with line breaks as spaces («no puedo⏎respirar»); the breaks, at the same positions, only cut clauses.
        var text = Spaces().Replace(Plain(body.Length > 4096 ? body[^4096..] : body), m => m.Value.Contains('\n') ? "\n" : " "); var flat = text.Replace('\n', ' ');
        if (Suicide().IsMatch(flat)) return true;
        // A recent ingestion is not resolved by time: poisoning skips the past rules, like suicide, but a negation still holds.
        if (Current(text, flat, Poisoning(), timed: false)) return true;
        Regex[] signs = [ChestPain(), Breathing(), Unconscious(), Seizure(), Bleeding(), OneSide(), AllergicAirway(), Cardiac(), Collapse()];
        return signs.Any(sign => Current(text, flat, sign)) || (Allergy().IsMatch(flat) && Current(text, flat, Swelling()));
    }
    /// <summary>Any match that is stated now. The scan restarts one character on, so a negated match («no es dolor…») does not hide a later
    /// one that starts inside it («…el pecho me aprieta»).</summary>
    static bool Current(string text, string flat, Regex sign, bool timed = true)
    {
        for (var m = sign.Match(flat); m.Success; m = m.Index + 1 < flat.Length ? sign.Match(flat, m.Index + 1) : Match.Empty)
            if (Stated(text, m, timed)) return true;
        return false;
    }
    static bool Stated(string text, Match m, bool timed)
    {
        var before = text[..m.Index]; var clause = Clause().Split(before)[^1];
        // Negated: «no», «sin», «ni» or «tampoco» followed only by pronouns and auxiliaries up to the sign («no me duele», «no tengo dolor»).
        // «No aguanto el dolor» negates «aguanto», not the pain; «no sé si…» is doubt, and so is «no se me duele», which only reads as «no sé, me duele».
        var value = text.Substring(m.Index, m.Length);
        if (Negated().IsMatch(clause) && !(NoSe().IsMatch(clause) && NotReflexive().IsMatch(value))) return false;
        if (!timed) return true;
        // Resolved: «no ha vuelto» before it in its clause, or an explicit, affirmative recovery of the patient right after it, in this clause or the
        // next one («…pero ya estoy bien», «Me desmayé. Ya estoy bien.»). Not a question, not of something else («ya estoy bien de la gripe»),
        // not of someone else («ya está bien mi hermano»); and «…pero sigue mareado» after it keeps the sign.
        var after = text[(m.Index + m.Length)..];
        if (Recovered().IsMatch(clause)) return false;
        if (Recovers().Match(after) is { Success: true } recovery && !Ongoing().IsMatch(SentenceOf(after[recovery.Length..]))) return false;
        // Past: a time gone by in the sign's own clause with no verb in the present there («tuvo un infarto hace dos años», but not
        // «hace dos días que me duele el pecho»), unless the sentence says it is still going («y no se me quita», «hoy», «peor»).
        var own = clause + value + Clause().Split(after)[0];
        if (!Past().IsMatch(own) || Present().IsMatch(own)) return true;
        var start = before.LastIndexOfAny(['.', '!', '?', '\n']) + 1;
        return Ongoing().IsMatch(text[start..m.Index] + value + SentenceOf(after));
    }
    static string SentenceOf(string text) => text.IndexOfAny(['.', '!', '?', '\n']) is var stop and >= 0 ? text[..stop] : text;
    static string Plain(string text) => new string(text.Normalize(System.Text.NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();

    /// <summary>Only a greeting: answered at once with the menu, without the model.</summary>
    public static bool IsGreeting(string body) => Greeting().IsMatch(body);

    /// <summary>A slot the patient tapped in the list of free hours: start, doctor, minutes and the doctor's name as shown.</summary>
    public static (string StartsAt, Guid Doctor, int Minutes, string Name)? SlotChoice(string body) =>
        Slot().Match(body.Trim()) is { Success: true } m && Guid.TryParse(m.Groups[2].Value, out var doctor) ? (m.Groups[1].Value, doctor, int.Parse(m.Groups[3].Value), Unsafe().Replace(m.Groups[4].Value, "").Trim()) : null;

    /// <summary>A step of the guided booking: the day, then the doctor (an id, «*» any, «+» another, «?» not asked yet)
    /// and the part of the day («M», «T» or «?»).</summary>
    public static (string Day, string Doctor, string Band)? GuideChoice(string body) =>
        Guide().Match(body.Trim()) is { Success: true } m ? (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value) : null;

    /// <summary>The part of the day the patient named in so many words. «Mañana» alone is tomorrow, not the morning.</summary>
    public static string? SaidBand(string body) => BandTarde().IsMatch(body) ? "T" : BandManana().IsMatch(body) ? "M" : null;

    /// <summary>What the patient tapped on one of their own appointments: see it, cancel it, or move it (with the new slot once chosen).</summary>
    public static (string Verb, Guid Appointment, string? StartsAt, Guid? Doctor, int? Minutes, string Name)? AppointmentChoice(string body) =>
        Appointment().Match(body.Trim()) is { Success: true } m && Guid.TryParse(m.Groups[2].Value, out var appointment)
            ? (m.Groups[1].Value, appointment, m.Groups[3].Success ? m.Groups[3].Value : null, Guid.TryParse(m.Groups[4].Value, out var doctor) ? doctor : null, int.TryParse(m.Groups[5].Value, out var minutes) ? minutes : null, Unsafe().Replace(m.Groups[6].Value, "").Trim()) : null;

    /// <summary>The request is for someone else («registra a mi papá», «una cita a mi hijo»). A record or an appointment made from here
    /// would belong to that person and be tied to the sender's phone, so neither is offered.</summary>
    public static bool ForSomeoneElse(string body) => Other().IsMatch(body);

    /// <summary>The patient wants a prescription that does not exist yet. Re-sending the previous one would be a refill nobody authorised.</summary>
    public static bool AsksNewPrescription(string body) => Refill().IsMatch(body);

    /// <summary>A plain «sí» to the emergency question is an emergency; the model is not asked to judge it.</summary>
    public static bool Affirms(string body) => Yes().IsMatch(body);

    /// <summary>The reply tells the patient they are being transferred. The runtime then transfers them for real.</summary>
    public static bool ClaimsHandoff(string reply) => Transfer().Matches(reply).Any(match =>
    {
        // «¿Te paso con recepción?» is an offer the patient still has to accept, not an announcement.
        var from = reply.LastIndexOfAny(['.', '!', '?', ';', '\n'], match.Index) + 1; var to = reply.IndexOfAny(['.', '!', '?', ';', '\n'], match.Index + match.Length);
        return !(reply[from..match.Index].Contains('¿') || (to >= 0 && reply[to] == '?'));
    });

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
        // Same rule for money: a price or a discount that the catalogue, the guide or the staff never gave commits the hospital to an invented figure.
        // Only what a source wrote as money counts: the «30» of a slot's minutes does not make «$30» a price, and a list price of 25 does not
        // make «25 %» a discount. What the patient wrote is not a source either: «sí, cuesta $20» echoed back would be the hospital confirming it.
        // ponytail: figures marked as money only («$25», «25$», «USD 25», «25 dólares», «10 %», «10 pct»), and dollars only. An amount in words,
        // a figure with no mark («cuesta 40») and a range («entre 20 y 30 dólares», where only the 30 is seen) are left to the prompt and the evals.
        var figures = Money().Matches(grounding).Select(m => Figure(m.Value)).ToHashSet();
        if (Money().Matches(reply).Any(m => Figure(m.Value) is not { } figure || !figures.Contains(figure))) return "precio";
        return proposed && Done().IsMatch(reply) ? "confirmación" : null;
    }
    /// <summary>A money figure as a number and whether it is a percentage, so «$1,200», «1200.00 USD» and «1.200 dólares» are the same amount.
    /// A dot or a comma before exactly three final digits separates thousands, in either style; before any other count it is the decimal mark.
    /// The catalogue always writes two decimals, so «$25.000» is twenty-five thousand and never the «25.00 USD» of the list.
    /// Null when it is not a figure a source can back: forty digits, or one scaled in words («25 mil», «2 millones»). It then matches nothing.</summary>
    static (bool Percent, decimal Amount)? Figure(string money)
    {
        var text = Number().Match(money).Value; var cut = text.LastIndexOfAny(['.', ',']); var whole = cut < 0 || text.Length - cut - 1 == 3;
        var digits = (whole ? text : text[..cut]).Replace(".", "").Replace(",", "") + (whole ? "" : "." + text[(cut + 1)..]);
        return !money.Contains("mil", StringComparison.OrdinalIgnoreCase) && decimal.TryParse(digits, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var amount)
            ? (money.Contains('%') || money.Contains("ciento", StringComparison.OrdinalIgnoreCase) || money.Contains("pct", StringComparison.OrdinalIgnoreCase), amount) : null;
    }
    /// <summary>A clock time on the 24-hour scale, so «6:00 PM» and the guide's «18:00» are the same hour.</summary>
    static string Clock(Match time)
    {
        var hour = int.Parse(time.Groups[1].Value); var half = time.Groups[3].Value.ToLowerInvariant();
        if (half == "p" && hour < 12) hour += 12; else if (half == "a" && hour == 12) hour = 0;
        return hour + ":" + time.Groups[2].Value;
    }
    static string Compact(string text) => Spaces().Replace(text.ToLowerInvariant(), "");

    [GeneratedRegex(@"(?i)\b(hablar|comunicarme|comunicar|contactar)\b.{0,60}\b(doctor|doctora|médico|medico|humano|persona)\b")] private static partial Regex Person();
    [GeneratedRegex(@"(?i)\b(emergencia|sobredosis|suicidio)\b")] private static partial Regex Emergency();
    // MR-35, one pattern per sign of the approved list, on text without accents, in lower case and with single spaces; line breaks stay (AlarmSign).
    // ponytail: a closed list of phrasings, misspellings included; one it does not know is left to the prompt and the evals, the second net.
    // «Sino el pecho» and «pero el pecho sí» carry the pain said just before in the sentence; a «no» between «pecho» and «duele» negates it.
    [GeneratedRegex(@"\b(?:dolor(?:cito|es)?|duele|duelen|duela|duelan|doler\w*|dolio|dolia|doliendo|opresion|presion|oprime|oprim\w*|aprieta|apretad[oa]|punzadas?)\b(?:\W+(?!(?:sino|pero|aunque)\b)\w+){0,5}?\W+pecho\b"
        + @"|\bpecho\b(?:\W+(?!(?:no|ya|sino|pero)\b)\w+){0,3}?\W+(?:duele|doliendo|apretado|oprimido|aprieta|oprime)\b|(?<=\b(?:duele|duelen|dolor)\b[^.!?\n]{0,60})\b(?:sino|pero) (?:en )?el pecho\b(?! no\b)")] private static partial Regex ChestPain();
    [GeneratedRegex(@"\b(?:no|tampoco|ni) (?:\w+ ){0,2}?(?:respiro\b(?! bien)|respir(?!o\b)\w*)|\bsin poder respirar\b|\bcuesta (?:\w+ )?respirar\b|\bdificultad (?:\w+ )?(?:para )?respirar\b|\bfalt[ae] (?:el )?aire\b|\bno (?:me|le|te|nos) entra (?:el )?aire\b|\bah?og\w*|\basfixi\w*")] private static partial Regex Breathing();
    [GeneratedRegex(@"\bdesma(?:y|ll)\w*|\bdesvanec\w*|\bperd\w* (?:el )?(?:conocimiento|sentido)\b|\bperd\w* (?:la )?conciencia\b|\binconsciente\b|\bno (?:me |le )?reacciona\b|\bse (?:me |le )?priv[oa]\w*"
        + @"|\bno (?:se )?(?:ha |han )?despert\w*|\bno despierta\b|\bno (?:lo |la |le )?(?:puedo|podemos|puede|pude|pudimos) despertar\w*|\bno quiere despertar\w*|\bno responde\b(?! (?:a|al|el|la|los|las|mis|sus|mensajes?|llamadas?|whatsapp)\b)")] private static partial Regex Unconscious();
    [GeneratedRegex(@"\bconvul[sc]\w*|\bataques? (?:epileptic\w*|de epilepsia)\b|\b(?:le|me|te|les|nos) (?:dio|dan|da|dieron|vino|vienen|esta dando|estan dando) (?:un |unos )?ataques?\b(?! (?:de (?:risa|tos|panico|ansiedad|nervios|celos|asma|tos)|al corazon|cardiac\w*)\b)|\bespuma (?:por|de|en) la boca\b")] private static partial Regex Seizure();
    [GeneratedRegex(@"\bh?emorragi\w*|\bdesangr\w*|\bsangr\w*(?:\W+\w+){0,5}?\W+(?:mucho|muchisimo|musho|bastante|abundante\w*|chorros?|sin parar|no (?:\w+ ){0,2}?(?:para|paro|detiene|deja|corta|quita))\b|\b(?:mucha|muchisima|bastante|abundante) sangre\b|\bno (?:me |le |se )?(?:ha )?(?:para|parado|deja|dejado|detiene|detenido)\w* de sangr\w*")] private static partial Regex Bleeding();
    // Weakness or numbness of one side (with tingling of one arm, added by the owner), a drooping face, or not able to speak.
    // «No puedo hablar» is a busy patient when the same sentence names work, the phone, a meeting; said of someone else it always fires.
    [GeneratedRegex(@"\b(?:debil\w*|adormec\w*|dormid[oa]|durmio|duerme|entumid\w*|entumec\w*|parali\w*|sin fuerza|hormigue\w*)\b(?:\W+\w+){0,5}?\W+(?:(?:lado|mitad)\b|medio cuerpo\b|media cara\b|(?:brazo|pierna|mano) (?:izquierd|derech)\w*)"
        + @"|\b(?:brazo|pierna|mano) (?:izquierd|derech)\w* (?:\w+ )?(?:debil\w*|adormec\w*|dormid[oa]|entumid\w*|entumec\w*|parali\w*|sin fuerza)"
        + @"|\bno (?:\w+ )?(?:puedo|puede|podia|pudo) mover (?:\w+ ){0,3}?(?:(?:lado|mitad)\b|medio cuerpo\b|(?:brazo|pierna|mano) (?:izquierd|derech)\w*)|\bno (?:me |le )?(?:siento|siente) (?:\w+ ){0,3}?(?:(?:lado|mitad)\b|medio cuerpo\b|(?:brazo|pierna|mano) (?:izquierd|derech)\w*)"
        + @"|\bcara (?:\w+ )?(?:caid|torcid|chuec|ladead|desviad)\w*|\b(?:cayo|torcio|ladeo) (?:la |media )?(?:cara|boca)\b|\bboca (?:\w+ )?(?:torcid|chuec|caid|ladead)\w*|\bboca\b(?:\W+\w+){0,2}?\W+de lado\b"
        + @"|\bno (?:\w+ )?(?:puede|podia|pudo|logra) hablar\b(?! con\b)|\bno (?:\w+ )?(?:puedo|logro) hablar\b(?! con\b)(?!(?:\W+\w+){0,6}?\W+(?:trabajo|telefono|llamada|llamar|ocupad[oa]|reunion|clase|manejando)\b)|\bhabla\w* (?:\w+ )?(?:raro|enredad\w*|trabad\w*|arrastrad\w*)|\blengua trabada\b")] private static partial Regex OneSide();
    [GeneratedRegex(@"\bsobre ?dosis\b|\bintox\w*|\bintosic\w*|\benvenen\w*|\b(?:tom|trag|beb|ingir|ingier)\w* (?:\w+ ){0,4}?(?:veneno|cloro|lejia|insecticida|pesticida|plaguicida|raticida|herbicida|gramoxone|paraquat|creolina|matarratas|gasolina|kerosene|querosene)\b"
        + @"|\b(?:tom|trag|ingir|ingier)\w* (?:\w+ ){0,2}?(?:como |casi |unas |mas de )?(?:\d{2,}|diez|once|doce|quince|veinte|treinta|cuarenta|cincuenta|cien|un monton|muchas|demasiadas|todas las|tod[oa] (?:el|la) \w+|media caja|medio (?:frasco|bote|blister)|(?:un|una|el|la) (?:frasco|caja|bote|blister|puno)(?: (?:entero|entera|completo|completa))?) (?:de )?(?:\w+ )?(?:pastillas|pildoras|tabletas|medicinas|medicamentos|capsulas)\b"
        + @"|\bse (?:comio|comieron) (?:\w+ ){0,3}?(?:pastillas|pildoras|tabletas|medicinas|medicamentos|capsulas)\b")] private static partial Regex Poisoning();
    [GeneratedRegex(@"\banafila\w*|\bcerrando la garganta\b|\bse (?:me |le )?cierra la garganta\b|\bgarganta (?:se (?:me |le )?)?(?:cerrada|cierra|cerrando)\b")] private static partial Regex AllergicAirway();
    // Added by the owner on 2026-10-17, with the medical synonyms the coordinator counted as the same signs. «Derrame» is a stroke unless in an eye or a knee.
    [GeneratedRegex(@"\bi[nm]fart\w*|\bpre ?infart\w*|\bparo cardi\w*|\bataques? (?:al|del) corazon\b|\bataques? cardiac\w*|\bembolia\w*|\bacv\b|\bictus\b|\bderrames?\b(?! (?:en|de) (?:(?:el|la|los|las|un|una|mi|su) )?(?:ojos?|rodillas?|liquido|petroleo|aceite)\b)")] private static partial Regex Cardiac();
    // Added by the owner on 2026-10-17: turning blue (not a bruise on a limb: «se le puso morado el dedo»), and a fall with no getting up.
    [GeneratedRegex(@"\bse (?:me |le )?(?:puso|pone|pusieron|esta poniendo) morad(?:it)?[oa]s?\b(?! (?:el|la|los|las|mi|su|un|una) (?:dedos?|brazos?|piernas?|ojos?|rodillas?|manos?|pies?|unas?|golpe|moreton)\b)|\blabios? morad\w*|\bse ca(?:y|ll)o\b(?:\W+\w+){0,4}?\W+no (?:se )?(?:puede )?levant\w*")] private static partial Regex Collapse();
    [GeneratedRegex(@"\bsui[cs]id\w*|\b(?:quiero|kiero|quisiera) morir\w*|\bme (?:quiero|kiero) morir\b|\bganas de morir\w*|\b(?:quiero|quisiera|kiero) estar muert[oa]\b|\bquit\w* la vida\b|\bmatarme\b|\bme (?:quiero|kiero|voy a) matar\b|\bme mato\b|\bmejor me muero\b|\bojala (?:y )?me muera\b|\bno (?:quiero|kiero) (?:seguir )?viv\w*|\bno vale la pena (?:seguir )?vivir\b|\bacabar con mi vida\b|\bhacerme dano\b|\bme hago dano\b|\blastimarme\b|\bme estoy lastimando\b|\bautolesi\w*"
        + @"|\b(?:cortarme|me corte|me corto) (?:las venas|los brazos|las munecas)\b|\bme (?:estoy|quiero|voy a) cort(?:ar\w*|ando)\b(?! (?:el|la|las|los|mi|mis) (?:pelo|cabello|unas|fleco|flequillo|barba|melena)\b)")] private static partial Regex Suicide();
    // A severe allergy is the reaction plus swelling of the face or throat: «garganta inflamada» alone is a sore throat, a swollen face alone a tooth.
    [GeneratedRegex(@"\b(?:alerg\w*|reaccion\w*|pico\b|picadura\w*|piquete\w*|ronchas?|anafila\w*)")] private static partial Regex Allergy();
    [GeneratedRegex(@"\b(?:hinch\w*|inflam\w*)\b(?:\W+\w+){0,4}?\W+(?:cara|garganta|labios?|lengua|ojos|parpados|boca)\b|\b(?:cara|garganta|labios?|lengua|ojos|parpados|boca)\b(?:\W+\w+){0,2}?\W+(?:hinchad|inflamad)\w*")] private static partial Regex Swelling();
    [GeneratedRegex(@"[.,;:!?\n]|\b(?:y|e|pero|aunque|sino)\b")] private static partial Regex Clause();
    [GeneratedRegex(@"\b(?:no|sin|ni|tampoco)(?: (?:me|te|le|se|nos|les|lo|la|los|las|he|ha|has|han|hemos|habia|tengo|tiene|tienes|tenia|tuve|tuvo|siento|sentia|hay|esta|estoy|estaba|es|era|fue|un|una|el|ningun|ninguna|nada|de|mucho|mucha)){0,3} ?$")] private static partial Regex Negated();
    [GeneratedRegex(@"\bno se (?:me|le|te|nos) $")] private static partial Regex NoSe();
    [GeneratedRegex(@"^(?:duel|dolor|falt|cuest|aprieta|oprim)")] private static partial Regex NotReflexive();
    [GeneratedRegex(@"\bno (?:me |le |nos )?(?:ha|han) vuelto\b|\bno (?:me |le )?volvi[oa]\b")] private static partial Regex Recovered();
    [GeneratedRegex(@"^ ?(?:(?:[.,;:!\n]|\b(?:pero|y|e|aunque)\b) ?){0,3}(?:ahora |ahorita )?ya (?:(?:estoy|esta|estamos) bien|(?:me|se) (?:siento|siente) bien|se (?:me |le )paso|me recupere|se recupero)\b(?= ?(?:$|[.,;:!\n]|\b(?:y|pero|gracias|un poco)\b))")] private static partial Regex Recovers();
    [GeneratedRegex(@"\b(?:ayer|anoche|antier|anteayer)\b|\bhace (?:un|una|unos|unas|dos|tres|cuatro|cinco|seis|siete|ocho|nueve|diez|varios|varias|muchos|muchas|\d+) (?:dias?|semanas?|mes|meses|anos?)\b|\b(?:la semana|el mes|el ano) pasad[oa]\b|\bde (?:nin[oa]|chiquit[oa]|pequen[oa]|joven)\b|\bhace (?:mucho )?tiempo\b|\bel otro dia\b")] private static partial Regex Past();
    [GeneratedRegex(@"\b(?:duele|duelen|doliendo|tengo|tiene|tienes|siento|siente|estoy|esta|estan|sigue|sigo|puedo|puede|va|voy|aprieta|ahogo|ahoga|falta|cuesta|sangra|sangro|sale|convulsiona|reacciona|despierta|respira|habla|da|dan|entra|duerme)\b")] private static partial Regex Present();
    [GeneratedRegex(@"\b(?:desde|todavia|aun|sigue|sigo|siguen|continua|ahora|ahorita|hoy|peor|mas|tambien|otra vez|de nuevo)\b|\bno (?:se )?(?:me |le )(?:ha )?quit\w*|\bno (?:ha )?despert\w*|\bno despierta\b")] private static partial Regex Ongoing();
    [GeneratedRegex(@"(?i)\b(?:otra|nueva)\s+receta\b|\bresurt\w*|\bse\s+me\s+(?:acab[oó]|termin[oó])\b|\brec[eé]t[ae]me\b|\brenovar\s+(?:la|mi)\s+receta\b")] private static partial Regex Refill();
    [GeneratedRegex(@"(?i)contactos?\s+de\s+emergencias?")] private static partial Regex EmergencyContact();
    [GeneratedRegex(@"(?i)\bemergencias?\s*:")] private static partial Regex EmergencyLabel();
    [GeneratedRegex(@"(?i)\bno\s+(?:es|se\s+trata\s+de|tengo|hay)\s+(?:una\s+|ninguna\s+)?emergencias?\b|\bninguna\s+emergencia\b|\bsin\s+emergencia\b")] private static partial Regex NotAnEmergency();
    [GeneratedRegex(@"(?i)\b(?:voy|vamos|procedo)\s+a\s+(?:derivar|transferir|pasar|comunicar)\w*|\b(?:te|lo|la|le)\s+(?:derivo|transfiero|paso|comunico|derivar[eé]|transferir[eé]|pasar[eé]|comunicar[eé])\b|\b(?:un|una)\s+(?:agente|asesor|asesora|persona|miembro del equipo)\s+(?:te|lo|la|le)\s+atender[aá]")] private static partial Regex Transfer();
    [GeneratedRegex(@"(?i)(?:https?://|www\.)\S+")] private static partial Regex Link();
    [GeneratedRegex(@"(?i)\b\d+(?:[.,]\d+)?\s*(?:mg|mcg|µg|g|ml|ui|gotas?|tabletas?|pastillas?|c[aá]psulas?|comprimidos?|ampollas?|cucharadas?)\b|\bcada\s+\d+\s*(?:horas?|hrs?|h|d[ií]as?)\b")] private static partial Regex Dose();
    [GeneratedRegex(@"(?i)\b(?:qued[oó]|est[aá]|ha\s+sido|ha\s+quedado|fue)\s+(?:ya\s+)?(?:confirmad|agendad|reprogramad|cancelad|reservad|programad)[ao]\b|\b(?:he|hemos)\s+(?:confirmado|agendado|reprogramado|cancelado|reservado)\b|\b(?:cancel|agend|reprogram|reserv)é\b")] private static partial Regex Done();
    [GeneratedRegex(@"(?i)^\W*(s[ií]|sip|claro|correcto|afirmativo|as[ií] es|ok|okay|dale|de acuerdo|confirmo|conf[ií]rm[ae]la|conf[ií]rmalo|confirmar)\b")] private static partial Regex Yes();
    [GeneratedRegex(@"(?i)\b([01]?\d|2[0-3]):([0-5]\d)(?:\s*([ap])\.?\s?m\b)?")] private static partial Regex Time();
    [GeneratedRegex(@"(?i)^\W*(?:hola|holi|buenas|buen\s+d[ií]a|buenos\s+d[ií]as|buenas\s+tardes|buenas\s+noches|saludos|hi|hello)(?:\W+(?:buenas|buen\s+d[ií]a|buenos\s+d[ií]as|buenas\s+tardes|buenas\s+noches))?\W*$")] private static partial Regex Greeting();
    [GeneratedRegex(@"^CITA (\S{1,40}) ([0-9a-fA-F-]{36}) (\d{1,3})(?: (.{1,60}))?$")] private static partial Regex Slot();
    [GeneratedRegex(@"^VER (\d{4}-\d{2}-\d{2}) ([0-9a-fA-F-]{36}|[*+?]) ([MT?])$")] private static partial Regex Guide();
    [GeneratedRegex(@"(?i)\b(?:en|por|a)\s+la\s+tarde\b|\bde\s+tarde\b")] private static partial Regex BandTarde();
    [GeneratedRegex(@"(?i)\b(?:en|por)\s+la\s+ma[ñn]ana\b|\btemprano\b")] private static partial Regex BandManana();
    [GeneratedRegex(@"^(VERCITA|CANCELAR|MOVER) ([0-9a-fA-F-]{36})(?: (\S{1,40}) ([0-9a-fA-F-]{36}) (\d{1,3})(?: (.{1,60}))?)?$")] private static partial Regex Appointment();
    // ponytail: the closed list of relatives people actually name; «para una amiga de mi tía» is left to the prompt and the evals.
    [GeneratedRegex(@"(?i)\b(?:registr|inscrib|ag[eé]nd|cita|apunt|anot)\w*\b.{0,40}?\b(?:a|para|de)\s+(?:mi|mis|nuestr[oa])\s+(?:pap[aá]|mam[aá]|padres?|madre|espos[oa]|marido|mujer|hij[oa]s?|herman[oa]|abuel[oa]|t[ií][oa]|novi[oa]|pareja|suegr[oa]|niet[oa]|sobrin[oa]|amig[oa]|vecin[oa]|jef[ea]|beb[eé]|ni[ñn][oa])\b")] private static partial Regex Other();
    [GeneratedRegex(@"[^\p{L}\p{M} .'-]")] private static partial Regex Unsafe();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\d+(?:[.,]\d+)*")] private static partial Regex Number();
    // Four shapes: the sign or «USD» in front; the currency word behind; the sign behind («40$», unless it opens the next figure); a percentage.
    // «mil» and «millones» ride along so that Figure can refuse them.
    [GeneratedRegex(@"(?i)(?:\$|\bUSD)\s?\d[\d.,]*(?:\s*(?:mil|mill[oó]n(?:es)?)\b)?|\b\d[\d.,]*\s*(?:(?:mil|mill[oó]n(?:es)?)\s+(?:de\s+)?)?(?:d[oó]lar(?:es)?|dollars?|usd)\b|\b\d+(?:[.,]\d+)*\s?\$(?!\s?\d)|\b\d[\d.,]*\s?(?:%|(?:por\s?ciento|pct)\b)")] private static partial Regex Money();
}
