using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

/// <summary>The flows served without the model: the welcome menu, free hours and the guided booking, the latest prescription, and the patient's appointments.</summary>
public sealed partial class AgentRuntime
{
    /// <summary>The welcome menu as this hospital shows it.</summary>
    Choices MenuNow => Menu with { Options = Menu.Options.Where(option => attention.Shown.Contains(option.Id)).ToList() };
    /// <summary>What to do when a patient tells their symptoms: the hospital's choice, within what it has set up.</summary>
    string SymptomsRule() => attention.Symptoms == "person"
        ? "Si el paciente cuenta síntomas o cómo se siente, no supongas que es una emergencia: usa handoff sin urgent, y el sistema le ofrece pasar con una persona."
        : "Si el paciente cuenta síntomas o cómo se siente, no supongas que es una emergencia ni lo pases con nadie: atiéndelo. Dile en una frase que lo sientes, consulta hospital_availability "
            + (emergencyLine
                ? $"para ofrecerle una cita y, si en esta conversación todavía no se le ha preguntado, termina con la pregunta «{AgentGuard.EmergencyQuestion}». Usa handoff con urgent=true sólo cuando el paciente ya respondió que sí lo es."
                : "y ofrécele la cita más cercana. No le preguntes si es una emergencia ni uses handoff con urgent=true.");
    /// <summary>What to say about money: the hospital's choice. Off, the catalogue still answers whether a service exists; a price the guide itself states stays sayable, as before.</summary>
    string PricesRule() => attention.Prices == "on"
        ? "Precios: di el precio exactamente como lo devuelve hospital_services en listPrice, como «precio de lista» y con su moneda; nunca sumes, estimes ni calcules un monto con descuento. "
            + "Un precio que la guía diga también puedes decirlo, tal como está en la guía. Si un servicio viene sin listPrice y la guía tampoco dice su precio, lo confirma recepción: no es gratuito. "
            + "Si preguntan por el convenio de una empresa, usa hospital_agreement sólo con el nombre que el paciente escribió (si no lo ha dicho, pregúntaselo): di el porcentaje tal como viene y que recepción lo aplica al pagar; nunca enumeres empresas ni pruebes nombres."
        : "Precios: este hospital no da por WhatsApp los precios de su catálogo. Si la guía dice el precio, dilo tal como está en la guía; si no, nunca lo estimes: "
            + "en tu mensaje confirma si el servicio consta, di que el precio lo confirma recepción y pregunta si quiere que le pasen la consulta. Usa handoff sólo cuando el paciente acepte: "
            + "llamarlo antes cambia tu respuesta por una oferta genérica y el paciente no sabe si el servicio existe.";
    static readonly Choices Menu = new([new("AGENDAR", "Agendar cita", "Ver horarios libres"), new("MISCITAS", "Mis citas", "Consultar, cambiar o cancelar"), new("RECETA", "Mi receta", "Recibir mi última receta"), new("PERSONA", "Hablar con persona", "Pasar con recepción")], "Ver opciones");

    /// <summary>The free hours of the asked day or, when it has none, of the first later day that does. Fills the tappable list.</summary>
    /// <summary>A discipline as Hospital names it, in the words the patient hears. One this agent has no word for is not passed on raw.</summary>
    static string? DisciplineLabel(string discipline) => discipline switch { "medicine" => "medicina", "dentistry" => "odontología", "nutrition" => "nutrición", _ => null };
    /// <param name="labelled">For the model: each doctor also carries the discipline Hospital knows them by. The taps and the menu never need it and never ask for it.</param>
    async Task<JsonElement> Availability(string date, string? doctorId, CancellationToken ct, bool labelled = false)
    {
        Guid? doctor = Guid.TryParse(channel.DoctorId, out var fixedDoctor) ? fixedDoctor : Guid.TryParse(doctorId, out var id) ? id : null;
        // One call answers «the soonest»: the requested day or, if it has no free slot, the first later day that does.
        // Slots arrive labelled with the hospital's local date, weekday and time, so the model never does calendar arithmetic.
        // For today or tomorrow the query starts today, so «nothing in the next 8 hours» is a fact about now.
        var day = DateOnly.Parse(date); var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime); var soon = day <= today.AddDays(1);
        var options = await hospital.GetAvailabilityAsync(scope.Id, soon && day > today ? today : day, day.AddDays(13), doctor, ct);
        foreach (var professional in options.Professionals) doctors[professional.ClinicianId] = professional.ClinicianName;
        var free = options.Professionals.SelectMany(p => p.Days.SelectMany(d => d.Slots).Where(slot => slot.Offered && slot.TakenBy == 0).Select(slot => (Doctor: p, Slot: slot, Local: TimeZoneInfo.ConvertTime(slot.StartsAt, zone)))).Where(x => x.Slot.StartsAt > DateTimeOffset.UtcNow).ToList();
        // Only someone who wanted today or tomorrow is asked: for a date later in the week the question is noise.
        if (soon && attention.NoSlotSoon == "ask") askEmergency = free.Count == 0 || free.Min(x => x.Slot.StartsAt) > DateTimeOffset.UtcNow.AddHours(AgentGuard.UrgentWindowHours);
        // Narrowed here as well as in the query: the first day with room has to be that doctor's.
        readDoctor = doctor;
        free = free.Where(x => DateOnly.FromDateTime(x.Local.DateTime) >= day && (doctor is null || x.Doctor.ClinicianId == doctor)).ToList();
        if (free.Count == 0) return Result(new { requestedDate = date, available = false, note = "Sin horarios publicados en los 14 días desde esa fecha." });
        var first = free.Min(x => DateOnly.FromDateTime(x.Local.DateTime)); firstDay = first;
        dayFree = free.Where(x => DateOnly.FromDateTime(x.Local.DateTime) == first).OrderBy(x => x.Slot.StartsAt).ToList();
        // A registered patient taps one of these and gets the proposal straight away; without a record the tap opens the registration form.
        // So someone without a record who asks for another person gets no list: the form would register that person under this phone.
        var preferences = await db.ContactMemories.Where(x => x.ContactId == contact.Id).ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        if (contact.PatientId is not null || !forOther) slots.AddRange(free.Where(x => DateOnly.FromDateTime(x.Local.DateTime) == first).OrderBy(x => AgentMemory.Distance(preferences, x.Doctor.ClinicianName, x.Local)).ThenBy(x => x.Slot.StartsAt).Take(10).Select(x =>
            new Choice($"CITA {x.Local:yyyy-MM-ddTHH:mm:sszzz} {x.Doctor.ClinicianId:D} {x.Slot.DurationMinutes} {x.Doctor.ClinicianName}".TrimEnd(), $"{Weekdays[(int)x.Local.DayOfWeek][..3]} {x.Local.Day} {Months[x.Local.Month - 1][..3]} {x.Local:HH:mm}", x.Doctor.PlaceName.Length > 0 && x.Doctor.ClinicianName.Length > 0 ? $"{x.Doctor.ClinicianName} · {x.Doctor.PlaceName}" : x.Doctor.ClinicianName + x.Doctor.PlaceName)));
        // The model is told when the list was withheld: its instructions say a list always goes with the hours.
        var note = (first == day ? "" : "La fecha pedida no tiene horarios libres; estos son los del primer día siguiente con agenda. Dilo así al paciente. ")
            + (contact.PatientId is null && forOther ? "No se adjunta lista: desde este número sólo se agenda a quien escribe. Explícalo; no pidas tocar un horario." : "");
        var found = Result(new
        {
            requestedDate = date, date = first.ToString("yyyy-MM-dd"), weekday = Weekdays[(int)first.DayOfWeek], isRequestedDate = first == day,
            note = note.Length > 0 ? note.Trim() : null,
            doctors = free.Where(x => DateOnly.FromDateTime(x.Local.DateTime) == first).GroupBy(x => x.Doctor).Select(g => new { doctorId = g.Key.ClinicianId, doctor = g.Key.ClinicianName, place = g.Key.PlaceName, slots = g.OrderBy(x => x.Slot.StartsAt).Select(x => new { startsAt = x.Local.ToString("yyyy-MM-ddTHH:mm:sszzz"), time = x.Local.ToString("HH:mm"), until = x.Local.AddMinutes(x.Slot.DurationMinutes).ToString("HH:mm"), durationMinutes = x.Slot.DurationMinutes }) })
        });
        // «¿Tienen dentista?» is answered from what Hospital says each listed doctor is, never from a name or a title. A doctor Hospital does not
        // know the discipline of, or a Hospital that does not answer this yet, leaves the result exactly as it was.
        if (!labelled || await hospital.ClinicianDisciplines(scope.Id, ct) is not { Count: > 0 } disciplines) return found;
        var labels = JsonSerializer.SerializeToNode(found)!;
        foreach (var listed in labels["doctors"]!.AsArray())
            if (Guid.TryParse(listed!["doctorId"]!.ToString(), out var who) && disciplines.TryGetValue(who, out var discipline) && DisciplineLabel(discipline) is { } label) listed["discipline"] = label;
        return Result(labels);
    }

    /// <summary>«Agendar cita» from the menu: the first free hours, as a list to tap.</summary>
    /// <param name="lead">Said first. When it is set (the patient was just registered) an empty agenda is not a reason to hand off.</param>
    async Task OfferSlots(bool asked, string? emergencyPhone, CancellationToken ct, string lead = "")
    {
        JsonElement found = default;
        try { found = await Availability(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).ToString("yyyy-MM-dd"), null, ct); Trace("hospital_availability"); }
        catch (Exception ex) when (ex is HttpRequestException or HospitalIntegrationException) { if (lead.Length == 0) { await OfferPerson("No se pudo consultar la agenda del hospital.", "Disculpa, no pude consultar la agenda en este momento.", ct); return; } }
        if (slots.Count == 0 && lead.Length > 0) { await conversations.Send(conv.Id, lead + "\n¿Para qué día y hora quieres tu cita?", "agent", "agent:" + job.Id, ct: ct); return; }
        if (slots.Count == 0) { await OfferPerson("El paciente quiere agendar y no hay horarios publicados en los próximos 14 días.", "No hay horarios publicados en los próximos 14 días.", ct); return; }
        var day = DateOnly.ParseExact(found.GetProperty("date").GetString()!, "yyyy-MM-dd");
        var ask = askEmergency && !asked;
        var emergency = ask ? $"\n\nNo tengo horarios en las próximas {AgentGuard.UrgentWindowHours} horas.\n{AgentGuard.EmergencyQuestion} Si lo es, elige *Emergencia* en la lista." : "";
        // Two doctors with a full day do not fit in a ten-row list: the patient narrows it first. With that question on the table
        // the emergency is a statement, not a second question: a tap has to mean one thing.
        if (await NextQuestion("?", "?", ct) is { } question)
        {
            var options = ask ? WithEmergency(question.Choices) : question.Choices;
            await Reply(lead + $"Hay espacio el *{Weekdays[(int)day.DayOfWeek]} {day.Day} de {Months[day.Month - 1]}*.\n\n" + question.Text + (ask ? Urgent(options, emergencyPhone) : ""), ct, options); return;
        }
        var text = lead + $"Hay espacio el *{Weekdays[(int)day.DayOfWeek]} {day.Day} de {Months[day.Month - 1]}*.\nToca *Ver horarios* para elegir, o dime qué otro día prefieres." + emergency;
        var list = new Choices(slots, "Ver horarios");
        if (ask) Hold("question", "emergency", "", QuestionLife);
        await Reply(text, ct, ask ? WithEmergency(list) : list);
    }

    /// <summary>The emergency as one more thing to tap beside the options already offered. A WhatsApp list holds ten rows.</summary>
    static Choices WithEmergency(Choices choices) => choices with { Options = [.. choices.Options.Take(9), new Choice("EMERGENCIA", "Emergencia")] };
    /// <summary>Said beside another question: where to tap, and the hospital's number when there is one.</summary>
    static string Urgent(Choices choices, string? phone) =>
        $"\n\nSi es una emergencia y no puedes esperar, {(choices.Options.Count > 3 || choices.Options.Any(option => option.Description is not null) ? "elige *Emergencia* en la lista" : "toca *Emergencia*")}{(phone is { Length: > 0 } ? $" o llama al *{phone}*" : "")}.";

    /// <summary>Whether the hour of one of this patient's appointments has already come.</summary>
    async Task<bool> HasPassed(Guid appointment, CancellationToken ct)
    {
        // ponytail: Hospital answers 31 days per query, so this sees the last 30; an older appointment is not caught here. The rule belongs in Hospital's own cancel.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        var range = await hospital.GetPatientAppointmentRangeAsync(scope.Id, contact.PatientId!.Value, contact.Phone, today.AddDays(-30), today, ct);
        // Only what Hospital says has passed is refused: an answer that cannot be read leaves the decision to Hospital's own cancel.
        return range.ValueKind == JsonValueKind.Object && range.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array
            && rows.EnumerateArray().Any(row => row.TryGetProperty("appointmentId", out var id) && id.TryGetGuid(out var one) && one == appointment && row.TryGetProperty("scheduledStart", out var at) && at.TryGetDateTimeOffset(out var start) && start <= DateTimeOffset.UtcNow);
    }

    /// <summary>A step of the guided booking the patient tapped: the day, the doctor (one, «any», «another», or not asked yet) and the part of the day.</summary>
    async Task Guide((string Day, string Doctor, string Band) step, CancellationToken ct)
    {
        try { await Availability(step.Day, Guid.TryParse(step.Doctor, out var chosen) ? chosen.ToString() : null, ct); Trace("hospital_availability"); }
        catch (Exception ex) when (ex is HttpRequestException or HospitalIntegrationException) { await OfferPerson("No se pudo consultar la agenda del hospital.", "Disculpa, no pude consultar la agenda en este momento.", ct); return; }
        if (dayFree.Count == 0 || firstDay is not { } day) { await OfferPerson("El paciente quiere agendar y no hay horarios publicados en los próximos 14 días.", "No hay horarios publicados en los próximos 14 días.", ct); return; }
        if (await NextQuestion(step.Doctor, step.Band, ct) is { } question) { await conversations.Send(conv.Id, question.Text, "agent", "agent:" + job.Id, ct: ct, choices: question.Choices); return; }
        // Nothing left to choose but the hour: one doctor, one part of the day.
        var hours = Narrowed(step.Doctor, step.Band);
        var band = hours.All(x => x.Local.Hour < 12) ? " en la *mañana*" : hours.All(x => x.Local.Hour >= 12) ? " en la *tarde*" : "";
        var who = hours[0].Doctor.ClinicianName.Length > 0 ? $" de *{hours[0].Doctor.ClinicianName}*" : "";
        await conversations.Send(conv.Id, $"Estos son los horarios{who} el *{Weekdays[(int)day.DayOfWeek]} {day.Day} de {Months[day.Month - 1]}*{band}.\nToca *Ver horarios* para elegir.", "agent", "agent:" + job.Id, ct: ct, choices: new(hours.Take(10).Select(Row).ToList(), "Ver horarios"));
    }

    /// <summary>The free hours of the day on offer that match what the patient has chosen so far.</summary>
    List<(HospitalProfessional Doctor, HospitalSlot Slot, DateTimeOffset Local)> Narrowed(string doctor, string band) =>
        dayFree.Where(x => (!Guid.TryParse(doctor, out var id) || x.Doctor.ClinicianId == id) && (band == "M" ? x.Local.Hour < 12 : band != "T" || x.Local.Hour >= 12)).ToList();

    Choice Row((HospitalProfessional Doctor, HospitalSlot Slot, DateTimeOffset Local) x) =>
        new($"CITA {x.Local:yyyy-MM-ddTHH:mm:sszzz} {x.Doctor.ClinicianId:D} {x.Slot.DurationMinutes} {x.Doctor.ClinicianName}".TrimEnd(), $"{Weekdays[(int)x.Local.DayOfWeek][..3]} {x.Local.Day} {Months[x.Local.Month - 1][..3]} {x.Local:HH:mm}", x.Doctor.PlaceName.Length > 0 && x.Doctor.ClinicianName.Length > 0 ? $"{x.Doctor.ClinicianName} · {x.Doctor.PlaceName}" : x.Doctor.ClinicianName + x.Doctor.PlaceName);

    /// <summary>What to ask next before listing hours, or null when only the hour is left to choose. A question that would
    /// choose nothing (one doctor, one part of the day) is never asked. Reads <see cref="dayFree"/>: call Availability first.</summary>
    async Task<(string Text, Choices Choices)?> NextQuestion(string doctor, string band, CancellationToken ct)
    {
        if (firstDay is not { } day) return null;
        var date = day.ToString("yyyy-MM-dd"); var when = $"*{Weekdays[(int)day.DayOfWeek]} {day.Day} de {Months[day.Month - 1]}*";
        string Name(HospitalProfessional p) => p.ClinicianName.Length > 0 ? p.ClinicianName : "Profesional sin nombre";
        Choice Pick(HospitalProfessional p, string part) => new($"VER {date} {p.ClinicianId:D} {part}", Name(p).Length > 24 ? Name(p)[..24] : Name(p), p.PlaceName.Length > 0 ? $"{Name(p)} · {p.PlaceName}" : Name(p));
        if (doctor is "?" or "+")
        {
            var doctors = dayFree.Select(x => x.Doctor).DistinctBy(p => p.ClinicianId).ToList();
            // Someone who has been here before is asked about the doctor they already know: the one of their last appointment or,
            // failing that, the one they once said they prefer. Only when there is another to choose — Hospital is not asked otherwise.
            if (attention.Booking == "doctor" && doctor == "?" && doctors.Count > 1 && await KnownDoctor(doctors, ct) is { } known)
                return ($"¿La quieres con el mismo doctor, *{Name(known)}*?", new([new($"VER {date} {known.ClinicianId:D} {band}", "Sí, el mismo"), new($"VER {date} + {band}", "Otro doctor"), new($"VER {date} * {band}", "Me da igual")]));
            if (attention.Booking == "doctor" && doctors.Count > 1)
                return ("¿Tienes un doctor de preferencia?\nTe muestro su horario del día.", new([.. doctors.Select(p => Pick(p, band)), new($"VER {date} * {band}", "Me da igual", "Cualquier doctor disponible")], "Ver doctores"));
            doctor = "*";
        }
        var free = Narrowed(doctor, "?");
        if (band == "?")
        {
            if (free.Any(x => x.Local.Hour < 12) && free.Any(x => x.Local.Hour >= 12))
                return ("¿Te queda mejor en la mañana o en la tarde?", new([new($"VER {date} {doctor} M", "En la mañana"), new($"VER {date} {doctor} T", "En la tarde")]));
            band = free.Any(x => x.Local.Hour >= 12) ? "T" : "M";
        }
        if (attention.Booking == "doctor" && doctor == "*" && Narrowed("*", band).Select(x => x.Doctor).DistinctBy(p => p.ClinicianId).ToList() is { Count: > 1 } available)
            return ($"Los doctores disponibles el {when} en la *{(band == "M" ? "mañana" : "tarde")}* son:", new(available.Select(p => Pick(p, band)).ToList(), "Ver doctores"));
        return null;
    }

    async Task<HospitalProfessional?> KnownDoctor(List<HospitalProfessional> doctors, CancellationToken ct)
    {
        if (contact.PatientId is not null && await LastDoctor(ct) is { } last && doctors.FirstOrDefault(p => p.ClinicianId == last) is { } seen) return seen;
        var preferred = await db.ContactMemories.Where(x => x.ContactId == contact.Id && x.Key == "doctor_preferido").ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        return preferred.Count == 0 ? null : doctors.FirstOrDefault(p => AgentMemory.Distance(preferred, p.ClinicianName, default) == 0);
    }

    /// <summary>The doctor of this patient's most recent appointment, cancelled ones aside.</summary>
    async Task<Guid?> LastDoctor(CancellationToken ct)
    {
        // ponytail: Hospital answers 31 days per query, so «before» is the 15 days either side of today; page further back if patients return after longer.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        try
        {
            var range = await hospital.GetPatientAppointmentRangeAsync(scope.Id, contact.PatientId!.Value, contact.Phone, today.AddDays(-15), today.AddDays(15), ct);
            return range.GetProperty("rows").EnumerateArray().Where(row => row.GetProperty("status").GetString()?.StartsWith("cancelled") != true && row.TryGetProperty("clinicianId", out _))
                .OrderByDescending(row => row.GetProperty("scheduledStart").GetDateTimeOffset()).Select(row => (Guid?)row.GetProperty("clinicianId").GetGuid()).FirstOrDefault();
        }
        catch (Exception ex) when (ex is HttpRequestException or HospitalIntegrationException) { return null; } // not knowing only skips the question
    }

    /// <summary>«Mi receta» from the menu: the latest signed prescription, as the document.</summary>
    async Task DeliverLatest(CancellationToken ct)
    {
        try
        {
            var sent = await SendPrescription(null, ct); Trace("send_latest_prescription");
            if (sent.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.False) await conversations.Send(conv.Id, "No tienes recetas emitidas disponibles. Si necesitas una, dime «quiero hablar con una persona» y te paso con recepción.", "agent", "agent:" + job.Id, ct: ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or HospitalIntegrationException) { await OfferPerson("No se pudo entregar la receta que pidió el paciente.", "Disculpa, no pude enviarte la receta en este momento.", ct); }
    }

    /// <summary>The patient's booked appointments from now on, as Hospital has them. Cancelled ones are not appointments any more.</summary>
    async Task<List<(Guid Id, DateTimeOffset Start, string Doctor)>> Upcoming(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        var range = await hospital.GetPatientAppointmentRangeAsync(scope.Id, contact.PatientId!.Value, contact.Phone, today, today.AddDays(30), ct);
        return range.GetProperty("rows").EnumerateArray().Where(row => row.GetProperty("status").GetString() == "booked" && row.GetProperty("scheduledStart").GetDateTimeOffset() > DateTimeOffset.UtcNow)
            .Select(row => (row.GetProperty("appointmentId").GetGuid(), row.GetProperty("scheduledStart").GetDateTimeOffset(), row.GetProperty("clinicianName").GetString() ?? "")).OrderBy(row => row.Item2).ToList();
    }
    string Short(DateTimeOffset start) { var local = TimeZoneInfo.ConvertTime(start, zone); return $"{Weekdays[(int)local.DayOfWeek][..3]} {local.Day} {Months[local.Month - 1][..3]} {local:HH:mm}"; }
    Task ShowAppointment((Guid Id, DateTimeOffset Start, string Doctor) appointment, CancellationToken ct, string lead = "") =>
        conversations.Send(conv.Id, $"{lead}*Cita:* {When(appointment.Start)}\n*Con:* {appointment.Doctor}\n\n¿Qué quieres hacer?", "agent", "agent:" + job.Id, ct: ct,
            choices: new([new($"MOVER {appointment.Id}", "Cambiar fecha"), new($"CANCELAR {appointment.Id}", "Cancelar cita"), new("MENU", "Seguir aquí")]));

    /// <summary>«Mis citas» from the menu.</summary>
    async Task MyAppointments(CancellationToken ct)
    {
        if (contact.PatientId is null) { await conversations.Send(conv.Id, "Aún no tienes expediente con nosotros.\n¿Quieres agendar tu primera cita?", "agent", "agent:" + job.Id, ct: ct, choices: new([new("AGENDAR", "Agendar cita"), new("MENU", "Seguir aquí")])); return; }
        List<(Guid Id, DateTimeOffset Start, string Doctor)> upcoming;
        try { upcoming = await Upcoming(ct); Trace("my_appointments"); }
        catch (Exception ex) when (ex is HttpRequestException or HospitalIntegrationException) { await OfferPerson("No se pudieron consultar las citas del paciente.", "Disculpa, no pude consultar tus citas en este momento.", ct); return; }
        if (upcoming.Count == 0) { await conversations.Send(conv.Id, "No tienes citas próximas.\n¿Quieres agendar una?", "agent", "agent:" + job.Id, ct: ct, choices: new([new("AGENDAR", "Agendar cita"), new("MENU", "Seguir aquí")])); return; }
        if (upcoming.Count == 1) { await ShowAppointment(upcoming[0], ct); return; }
        await conversations.Send(conv.Id, "Estas son tus próximas citas.\nToca una para cambiarla o cancelarla.", "agent", "agent:" + job.Id, ct: ct,
            choices: new(upcoming.Take(10).Select(a => new Choice($"VERCITA {a.Id}", Short(a.Start), a.Doctor)).ToList(), "Ver citas"));
    }

    /// <summary>What the patient tapped on one of their appointments. It must be one Hospital lists for this patient; then it is shown,
    /// or a cancellation or a change is proposed for the patient to confirm.</summary>
    async Task AppointmentAction((string Verb, Guid Appointment, string? StartsAt, Guid? Doctor, int? Minutes, string Name) choice, CancellationToken ct)
    {
        try
        {
            var upcoming = await Upcoming(ct);
            if (!upcoming.Any(a => a.Id == choice.Appointment)) { await conversations.Send(conv.Id, "Disculpa, esa cita ya no aparece en tu agenda.\n¿En qué más te ayudo?", "agent", "agent:" + job.Id, ct: ct, choices: MenuNow); return; }
            var appointment = upcoming.First(a => a.Id == choice.Appointment);
            if (choice.Verb == "VERCITA") { await ShowAppointment(appointment, ct); return; }
            if (reminded is { } remindedAt && remindedAt != appointment.Start) { await ShowAppointment(appointment, ct, "Tu cita cambió desde ese recordatorio. Ahora es así:\n\n"); return; }
            if (choice.Verb == "MOVER" && choice.StartsAt is null)
            {
                var found = await Availability(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).ToString("yyyy-MM-dd"), null, ct); Trace("hospital_availability");
                if (slots.Count == 0) { await OfferPerson("El paciente quiere cambiar una cita y no hay horarios publicados en los próximos 14 días.", "No hay horarios publicados en los próximos 14 días.", ct); return; }
                var day = DateOnly.ParseExact(found.GetProperty("date").GetString()!, "yyyy-MM-dd");
                await conversations.Send(conv.Id, $"Hay espacio el *{Weekdays[(int)day.DayOfWeek]} {day.Day} de {Months[day.Month - 1]}*.\nToca *Ver horarios* y elige la nueva fecha.", "agent", "agent:" + job.Id, ct: ct,
                    choices: new(slots.Select(slot => slot with { Id = $"MOVER {choice.Appointment} " + slot.Id["CITA ".Length..] }).ToList(), "Ver horarios")); return;
            }
            await (choice.Verb == "CANCELAR" ? Propose("cancel", null, choice.Appointment.ToString(), null, null, ct) : Propose("reschedule", choice.Doctor?.ToString(), choice.Appointment.ToString(), choice.StartsAt, choice.Minutes, ct));
            Trace("propose_action");
            await Reply(summary!, ct, Confirmation());
        }
        catch (ArgumentException) { await conversations.Send(conv.Id, "Disculpa, ese horario ya no está disponible.\nToca *Mis citas* para intentarlo de nuevo.", "agent", "agent:" + job.Id, ct: ct, choices: MenuNow); }
        catch (Exception ex) when (ex is HttpRequestException or HospitalIntegrationException) { await OfferPerson("No se pudo gestionar la cita del paciente en Hospital.", "Disculpa, no pude consultar tu cita en este momento.", ct); }
    }
}
