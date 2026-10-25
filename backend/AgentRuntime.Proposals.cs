using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

/// <summary>Proposals the patient confirms (appointments, registrations, prescriptions), what confirming does, and the handoff to a person.</summary>
public sealed partial class AgentRuntime
{
    // The card with a registration and its appointment has two ways back, and both are commands served without the model:
    // the data («Corregir datos») or the hour («Otro horario» is the menu's «Agendar cita»: the list again, and the tap reissues the card).
    Choices Confirmation() => new(withHour ? [new("CONFIRMAR " + proposal, "Confirmar"), new("CORREGIR " + proposal, "Corregir datos"), new("AGENDAR", "Otro horario")] : [new("CONFIRMAR " + proposal, "Confirmar"), new("otro", second ?? "Otro horario")]);
    /// <summary>A start the agenda can take: with an explicit offset (a bare local time would be read in the server's zone, not the hospital's) and still ahead.</summary>
    static DateTimeOffset? Future(string? startsAt) => startsAt is not null && Regex.IsMatch(startsAt, @"(Z|[+-]\d{2}:\d{2})$") && DateTimeOffset.TryParse(startsAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var start) && start > DateTimeOffset.UtcNow ? start : null;
    string When(DateTimeOffset start) { var local = TimeZoneInfo.ConvertTime(start, zone); return $"{Weekdays[(int)local.DayOfWeek]} {local.Day} de {Months[local.Month - 1]}{(local.Year == TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Year ? "" : " de " + local.Year)} a las {local:HH:mm}"; }

    /// <summary>The patient tapped a free slot. That is a complete request, so the proposal is created here, without the model.</summary>
    async Task ProposeTapped((string StartsAt, Guid Doctor, int Minutes, string Name) slot, string raw, CancellationToken ct)
    {
        try
        {
            if (contact.PatientId is null && attention.Unregistered == "person") { await OfferPerson(NewPatientReason, NewPatientMessage, ct); return; }
            if (contact.PatientId is null)
            {
                // Without a record the hour is only kept, with the form that asks for the data. Hospital is not asked anything here:
                // whether the hour is still free is checked when the patient confirms.
                var start = Future(slot.StartsAt) ?? throw new ArgumentException("startsAt debe ser una fecha futura ISO8601 con zona");
                // The same limits Hospital's client enforces when booking: an hour it would refuse is not kept through seven answers.
                if (slot.Doctor == Guid.Empty || slot.Minutes is < 5 or > 480) throw new ArgumentException("doctor o duración inválidos");
                // «Otro horario» on the card: the data is already given and reviewed, so the card goes out again with the new hour and the old one stops existing.
                if (await Cards(ct) is [var card] && Registration(card) is { } data)
                {
                    string Field(string name) => data.GetProperty(name).GetString()!;
                    db.PendingActions.Remove(card);
                    await ProposeRegistration(Field("givenNames"), Field("familyNames"), Field("birthDate"), Field("sex"), Field("emergencyName"), Field("emergencyRelationship"), Field("emergencyPhone"), ct, raw); Trace("propose_registration");
                    await Reply(summary!, ct, Confirmation()); return;
                }
                var (state, prompt, _) = Intake.Start(raw, When(start)); Trace("start_registration");
                Hold("intake", "", JsonSerializer.Serialize(state, Json), IntakeLife); await db.SaveChangesAsync(ct);
                await Reply(prompt, ct); return;
            }
            // What the patient said this appointment is for when the agent looked up these hours, if anything (the hours a proposal keeps: 15 minutes).
            var said = await Open("visit_kind").OrderByDescending(x => x.CreatedAt).Select(x => x.Code).FirstOrDefaultAsync(ct);
            await Propose("create", slot.Doctor.ToString(), null, slot.StartsAt, slot.Minutes, ct, said); Trace("propose_action");
        }
        catch (ArgumentException) { await conversations.Send(conv.Id, "Disculpa, ese horario ya no está disponible.\nDime qué día prefieres y vuelvo a consultar la agenda.", "agent", "agent:" + job.Id, ct: ct); return; }
        await Reply(summary!, ct, Confirmation());
    }
    /// <summary>The proposals of this conversation that can still be confirmed.</summary>
    Task<List<PendingAction>> Cards(CancellationToken ct) => Open("proposal").ToListAsync(ct);
    /// <summary>The data of a registration card, when the proposal is one.</summary>
    static JsonElement? Registration(PendingAction card) => JsonSerializer.Deserialize<JsonElement>(card.Body) is var p && p.GetProperty("action").GetString() == "register" ? p : null;

    /// <summary>«Corregir datos» on the card with a registration and its appointment: the card stops existing, with its data, and the form starts over keeping the hour.
    /// The registration card without an hour keeps its own button, which goes to the agent.</summary>
    async Task Correct(string code, CancellationToken ct)
    {
        if ((await Cards(ct)).FirstOrDefault(x => x.Code == code) is not { } card || Registration(card) is not { } p || !p.TryGetProperty("startsAt", out var chosen) || contact.PatientId is not null)
        { await conversations.Send(conv.Id, "Esa confirmación ya venció o no existe.\nDime qué necesitas y lo preparo de nuevo.", "agent", "agent:" + job.Id, ct: ct); return; }
        db.PendingActions.Remove(card);
        if (Future(chosen.GetString()) is not { } start) { await db.SaveChangesAsync(ct); await conversations.Send(conv.Id, "Disculpa, ese horario ya no está disponible.\nDime qué día prefieres y vuelvo a consultar la agenda.", "agent", "agent:" + job.Id, ct: ct); return; }
        // The same text a tap on the list carries, so the form keeps the hour exactly as if it had just been tapped.
        var raw = $"CITA {TimeZoneInfo.ConvertTime(start, zone):yyyy-MM-ddTHH:mm:sszzz} {p.GetProperty("doctorId").GetString()} {p.GetProperty("durationMinutes").GetInt32()} {(p.TryGetProperty("doctorName", out var name) ? name.GetString() : "")}".Trim();
        var (state, prompt, _) = Intake.Start(raw, When(start)); Trace("start_registration");
        Hold("intake", "", JsonSerializer.Serialize(state, Json), IntakeLife); await db.SaveChangesAsync(ct);
        await Reply(prompt, ct);
    }

    /// <summary>Stores the registration the patient still has to confirm; Hospital is not contacted here.</summary>
    async Task<JsonElement> ProposeRegistration(string givenNames, string familyNames, string birthDate, string sex, string emergencyContactName, string emergencyContactRelationship, string emergencyContactPhone, CancellationToken ct, string? slot = null)
    {
        if (contact.PatientId is not null) throw new ArgumentException("El contacto ya tiene expediente");
        if (proposal is not null) throw new ArgumentException("Una propuesta por turno");
        if (sex is not ("female" or "male")) throw new ArgumentException("Sexo registral inválido");
        var born = DateOnly.ParseExact(birthDate, "yyyy-MM-dd"); var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        if (born > today.AddYears(-18) || born < today.AddYears(-120)) return Result(new { error = "Por WhatsApp sólo se registran personas adultas con fecha de nacimiento válida. Deriva a recepción." });
        var payload = new JsonObject { ["action"] = "register", ["givenNames"] = Rules.Required(givenNames, 100), ["familyNames"] = Rules.Required(familyNames, 100), ["birthDate"] = birthDate, ["sex"] = sex, ["emergencyName"] = Rules.Required(emergencyContactName), ["emergencyRelationship"] = Rules.Required(emergencyContactRelationship, 60), ["emergencyPhone"] = Rules.TypedPhone(emergencyContactPhone) };
        // The hour tapped before the form travels in the same proposal, with the keys an appointment proposal uses: one card, one Confirmar.
        // An hour that passed while the form was being filled is left out, and the card is the registration alone.
        var booking = "";
        if (slot is not null && AgentGuard.SlotChoice(slot) is { } chosen && Future(chosen.StartsAt) is { } start)
        {
            payload["startsAt"] = start.ToString("O"); payload["doctorId"] = (Guid.TryParse(channel.DoctorId, out var channelDoctor) ? channelDoctor : chosen.Doctor).ToString(); payload["durationMinutes"] = chosen.Minutes;
            if (chosen.Name.Length > 0) payload["doctorName"] = chosen.Name;
            withHour = true; booking = $"*Cita:* {When(start)}{(chosen.Name.Length > 0 ? $"\n*Con:* {chosen.Name}" : "")}\n\n¿Confirmo tu registro y tu cita?";
        }
        var code = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3));
        db.PendingActions.RemoveRange(await Cards(ct)); // one card at a time: the one before stops existing
        Hold("proposal", code, payload.ToJsonString(Json), ProposalLife); await db.SaveChangesAsync(ct);
        summary = $"Revisa tus datos:\n\n*{payload["givenNames"]} {payload["familyNames"]}*\nNacimiento: {born.Day} de {Months[born.Month - 1]} de {born.Year}\nSexo: {(sex == "female" ? "femenino" : "masculino")}\nContacto de emergencia: {payload["emergencyName"]} ({payload["emergencyRelationship"]}), {payload["emergencyPhone"]}\n\n{(booking.Length > 0 ? booking : "¿Están correctos?")}";
        proposal = code; second = "Corregir datos"; return Result(new { confirmationRequired = true, instruction = "El sistema ya envía al paciente sus datos con el botón Confirmar. No escribas nada más." });
    }

    /// <summary>One answer of the registration form. The finished form becomes the same proposal the agent would make, and keeps no data behind.</summary>
    async Task IntakeStep(PendingAction form, string answer, CancellationToken ct)
    {
        var (next, prompt, options) = JsonSerializer.Deserialize<Intake>(form.Body, Json)!.Answer(answer, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime));
        // The form is working state: finished, it goes with its data. Answered, it waits on the next question, thirty minutes more.
        if (next.Minor || next.Complete) db.PendingActions.Remove(form); else { form.Body = JsonSerializer.Serialize(next, Json); form.ExpiresAt = DateTimeOffset.UtcNow + IntakeLife; held.Add(form); }
        if (next.Complete) Trace("propose_registration");
        await db.SaveChangesAsync(ct);
        if (next.Minor) { await OfferPerson("Registro por WhatsApp de una persona menor de 18 años: requiere tutor legal en recepción.", "Para registrar a una persona menor de 18 años hace falta su tutor,\ny eso se hace con recepción.", ct); return; }
        if (!next.Complete) { await Reply(prompt, ct, options); return; }
        await ProposeRegistration(next.GivenNames!, next.FamilyNames!, next.BirthDate!, next.Sex!, next.EmergencyName!, next.EmergencyRelationship!, next.EmergencyPhone!, ct, next.Slot);
        await Reply(summary!, ct, Confirmation());
    }

    /// <summary>The doctor as Hospital names them, never as the text of a tap says: the patient can write that text. Null when Hospital cannot say;
    /// the card then goes out without that line.</summary>
    async Task<string?> DoctorName(string? doctorId, DateTimeOffset? at, CancellationToken ct)
    {
        if (!Guid.TryParse(doctorId, out var id)) return null;
        if (!doctors.ContainsKey(id) && at is { } when)
            try { var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(when, zone).DateTime); foreach (var p in (await hospital.GetAvailabilityAsync(scope.Id, day, day, id, ct)).Professionals) doctors[p.ClinicianId] = p.ClinicianName; }
            catch (Exception ex) when (ex is HttpRequestException or HospitalIntegrationException) { }
        return doctors.TryGetValue(id, out var known) && known.Length > 0 ? known : null;
    }
    /// <summary>Stores a proposal the patient still has to confirm; nothing reaches the agenda here.</summary>
    /// <param name="kind">What the appointment is for, when the patient said it. Without it the kind is derived on confirming, as before.</param>
    async Task<JsonElement> Propose(string action, string? doctorId, string? appointmentId, string? startsAt, int? durationMinutes, CancellationToken ct, string? kind = null)
    {
        // Without a record the appointment comes with the registration, from an hour tapped on the list: the model is told so instead of being sent to reception.
        if (action == "create" && contact.PatientId is null) return Result(new { error = "Este contacto no tiene expediente: la cita no se propone desde aquí. Consulta hospital_availability y pídele que toque un horario de la lista; el sistema le pide sus datos y le envía una sola propuesta con su registro y su cita. No derives." });
        RequirePatient(contact); await hospital.GetVerifiedPatientAsync(scope.Id, contact.PatientId!.Value, contact.Phone, ct);
        if (action is not ("create" or "reschedule" or "cancel")) throw new ArgumentException("Acción inválida");
        // Only what a patient can say an appointment is for. First visit or follow-up stays derived on confirming: someone just registered is never booked
        // as a follow-up because the model said so. «same-day-urgent» is triage: it goes through the emergency question and handoff, never through a word the model picks.
        // A model that fills an optional argument with "" said nothing.
        if (string.IsNullOrWhiteSpace(kind)) kind = null; else if (kind is not ("procedure" or "results" or "paperwork")) throw new ArgumentException("Tipo de cita inválido");
        if (proposal is not null) throw new ArgumentException("Una propuesta por turno");
        var code = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3));
        var payload = new JsonObject { ["action"] = action }; if (doctorId is not null) payload["doctorId"] = doctorId; if (appointmentId is not null) payload["appointmentId"] = appointmentId; if (durationMinutes is not null) payload["durationMinutes"] = durationMinutes;
        if (action != "cancel")
        {
            var start = Future(startsAt) ?? throw new ArgumentException("startsAt debe ser una fecha futura ISO8601 con zona");
            payload["startsAt"] = start.ToString("O");
            summary = $"*{(action == "reschedule" ? "Nueva fecha" : "Cita")}:* {When(start)}";
        }
        if (Guid.TryParse(channel.DoctorId, out var channelDoctor)) payload["doctorId"] = channelDoctor.ToString();
        // The card names the appointment and the doctor. An existing appointment is looked up so a cancellation says which one it is;
        // if Hospital cannot say, the proposal still goes out without those lines.
        var who = await DoctorName(payload["doctorId"]?.GetValue<string>(), Future(startsAt), ct);
        var passed = false;
        if (Guid.TryParse(appointmentId, out var existing))
            try
            {
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
                passed = await HasPassed(existing, ct);
                var range = await hospital.GetPatientAppointmentRangeAsync(scope.Id, contact.PatientId!.Value, contact.Phone, today, today.AddDays(30), ct);
                foreach (var row in range.GetProperty("rows").EnumerateArray().Where(row => row.GetProperty("appointmentId").GetGuid() == existing))
                {
                    who ??= row.GetProperty("clinicianName").GetString();
                    if (action == "cancel") summary = $"*Cancelar cita:* {When(row.GetProperty("scheduledStart").GetDateTimeOffset())}";
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or HospitalIntegrationException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        // An hour that has passed is not cancelled or moved: what happened to it is whether the patient came, and Hospital records that.
        if (passed) { summary = null; return Result(new { error = "Esa cita ya pasó: no se puede cancelar ni cambiar. Díselo así al paciente y ofrécele agendar una nueva. No derives." }); }
        if (who is not null) payload["doctorName"] = who;
        // Kept only for a new appointment: moving one does not change what it is for. The card says it when it is not an ordinary visit.
        if (action == "create" && kind is not null) payload["kind"] = kind;
        var purpose = action != "create" ? null : kind switch { "procedure" => "Procedimiento", "results" => "Entrega de resultados", "paperwork" => "Trámite", _ => null };
        summary = (summary ?? "Cancelar tu cita") + (who is null ? "" : $"\n*Con:* {who}") + (purpose is null ? "" : $"\n*Motivo:* {purpose}") + "\n\n" + (action switch { "cancel" => "¿La cancelo?", "reschedule" => "¿La cambio?", _ => "¿La confirmo?" });
        db.PendingActions.RemoveRange(await Cards(ct)); // one card at a time: the one before stops existing
        Hold("proposal", code, payload.ToJsonString(), ProposalLife); await db.SaveChangesAsync(ct);
        proposal = code; second = action == "cancel" ? "No cancelar" : "Otro horario"; return Result(new { confirmationRequired = true, instruction = "El sistema ya envía al paciente la propuesta con su botón. No escribas nada más.", details = payload });
    }
    async Task<JsonElement> SendPrescription(Guid? requested, CancellationToken ct)
    {
        RequirePatient(contact);
        var selected = requested ?? await hospital.GetLatestIssuedPrescriptionIdAsync(scope.Id, contact.PatientId!.Value, contact.Phone, ct);
        if (selected is not { } prescription) return Result(new { available = false, instruction = "No hay recetas emitidas disponibles. No inventes una receta; ofrece ayuda de recepción." });
        var pdf = await hospital.GetPrescriptionPdfAsync(scope.Id, contact.PatientId!.Value, contact.Phone, prescription, ct);
        if (!await Active(conv.Id, revision, ct)) return Result(new { error = "Atención automática pausada" });
        using var stream = new MemoryStream(pdf);
        var mid = await kapso.Upload(channel.PhoneNumberId, stream, "receta.pdf", "application/pdf", ct);
        using var lease = await conversations.Lock(conv.Id, ct);
        if (!await Active(conv.Id, revision, ct)) return Result(new { error = "Atención automática pausada" });
        var sent = await conversations.Send(conv.Id, "Receta emitida por tu doctor", "agent", $"prescription:{job.Id}:{prescription}", mid, "document", ct);
        // Which prescription left, so the CRM history and Hospital's own export audit can be matched. The id is a reference, not clinical content.
        db.Activities.Add(new Activity { TenantId = scope.Id, ConversationId = conv.Id, ContactId = contact.Id, Kind = "prescription_delivered", Actor = "Agente", ActorRole = "agent_ai", Body = $"Receta emitida entregada por WhatsApp. Referencia Hospital: {prescription}." }); await db.SaveChangesAsync(ct);
        // Sending bumps the conversation revision; the rest of the turn continues on the new one.
        revision = conv.Revision; return Result(new { sent = sent.Status == "sent", status = sent.Status });
    }
    async Task Confirm(Conversation conv, Contact contact, string code, Job job, CancellationToken ct)
    {
        // Also while the patient waits for a person nobody has answered yet: the card went out then, so its Confirmar is served (criterio 62).
        using var lease = await conversations.Lock(conv.Id, ct); await db.Entry(conv).ReloadAsync(ct); if (!await Active(conv.Id,conv.Revision,ct, conv.Status == "human" && await HandedOverUnanswered(ct) is not null)) return;
        var proposal = await Open("proposal").Where(x => x.Code == code).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (proposal is null) { await conversations.Send(conv.Id, "Esa confirmación ya venció o no existe.\nDime qué necesitas y lo preparo de nuevo.", "agent", "confirm:" + job.Id, ct: ct); return; }
        using var doc = JsonDocument.Parse(proposal.Body); var p = doc.RootElement; var action = p.GetProperty("action").GetString();
        if (action != "register") RequirePatient(contact);
        // Consume before the external write; never replay uncertain operations (INV-6). The proposal keeps none of its data once used,
        // and the history says a confirmation was processed, and of what.
        db.Activities.Add(new Activity { TenantId = scope.Id, ContactId = contact.Id, ConversationId = conv.Id, Kind = "proposal_used:" + code, Actor = "Agente", ActorRole = "agent_ai", Body = new JsonObject { ["action"] = action, ["startsAt"] = p.TryGetProperty("startsAt", out var at) ? at.GetString() : null, ["appointmentId"] = p.TryGetProperty("appointmentId", out var which) ? which.GetString() : null }.ToJsonString() });
        await Consume(proposal, ct);
        // MR-41: what Hospital keys this confirmation's writes by; one proposal, one operation. From the proposal, never the turn or the job,
        // so the same confirmation sent again is answered with the original. The registration has its own key, of the same proposal.
        var idempotency = "agent:" + proposal.Id;
        // A registration made after tapping an hour carries the appointment in the same proposal: one decision, one Confirmar.
        var registered = action == "register"; var booking = action is "create" or "reschedule" || (registered && p.TryGetProperty("startsAt", out _)); string? wanted = null;
        // Whether a write to Hospital has started: only from then on can the operation have happened without us knowing.
        var written = false; Guid? reference = null;
        try
        {
            Guid doctor = default; DateTimeOffset start = default; var duration = 0; var timeZone = ""; var free = false; var overlap = false;
            if (booking)
            {
                doctor = p.GetProperty("doctorId").GetGuid(); start = p.GetProperty("startsAt").GetDateTimeOffset(); duration = p.GetProperty("durationMinutes").GetInt32();
                wanted = When(start) + (p.TryGetProperty("doctorName", out var asked) ? " con " + asked.GetString() : "");
                timeZone=await db.Tenants.Where(x=>x.Id==scope.Id).Select(x=>x.TimeZone).SingleAsync(ct);
                // 1. The hour is checked first and it is only a read: if this fails, nothing has been written. An hour already past counts as taken
                // (asking Hospital about it would throw), also for someone with a record.
                free = start > DateTimeOffset.UtcNow && await hospital.IsSlotAvailableAsync(scope.Id,doctor,start,duration,timeZone,ct);
            }
            if (registered)
            {
                // 2. POST of the patient, 3. verify and link. False means a possible duplicate or a contact already linked: nothing else happens.
                written = true; if (!await Register(conv, contact, p, wanted, job, "agent-register:" + proposal.Id, ct)) return;
                // The patient confirmed their data, so they are registered even when the hour is gone; the free hours go out with it, without being asked for.
                if (!booking || !free) { await OfferSlots(true, null, ct, booking ? "Ya te registré. Ese horario se acaba de ocupar.\n" : $"Listo{(FirstName(contact.Name) is { } welcome ? ", " + welcome : "")}, ya te registré.\n"); return; }
                action = "create"; // 4. the appointment, by the same path as any other: reachable only with the record created and linked in this turn
            }
            else if (booking && !free) { await OfferPerson("El horario propuesto se ocupó antes de confirmar.", "Ese horario se acaba de ocupar.\nToca *Seguir aquí* y luego *Agendar cita* para ver otros.", ct); return; }
            reference = p.TryGetProperty("appointmentId", out var existing) && existing.TryGetGuid(out var known) ? known : null;
            if (action is "cancel" or "reschedule" && reference is { } target && await HasPassed(target, ct))
            {
                await conversations.Send(conv.Id, $"Esa cita ya pasó, así que no se puede {(action == "cancel" ? "cancelar" : "cambiar")}.\n¿Quieres agendar una nueva?", "agent", "confirm:" + job.Id, ct: ct, choices: new([new("AGENDAR", "Agendar cita"), new("MENU", "Seguir aquí")]));
                return;
            }
            // What the patient said the appointment is for, when the proposal carries it; else a first visit when Hospital has no earlier appointment
            // the patient came to (MR-42, MR-43). Someone registered by this very confirmation has none, so Hospital is not asked. Only a read: nothing is written yet.
            var visit = action != "create" ? null : p.TryGetProperty("kind", out var kind) && kind.GetString() is { } said ? said
                : registered ? "first-visit" : await hospital.FirstOrFollowUpAsync(scope.Id, contact.PatientId!.Value, start, ct);
            written = true;
            if (action == "cancel") await hospital.CancelAppointmentAsync(scope.Id, contact.PatientId!.Value, contact.Phone, p.GetProperty("appointmentId").GetGuid(), ct);
            else
            {
                if (action == "reschedule") await hospital.RescheduleAppointmentAsync(scope.Id, contact.PatientId!.Value, contact.Phone, p.GetProperty("appointmentId").GetGuid(), doctor, start, duration, ct);
                else
                {
                    var created = await hospital.CreateAppointmentAsync(scope.Id, contact.Phone, new HospitalAppointmentCreate(contact.PatientId!.Value, doctor, start, duration, visit!), idempotency, ct);
                    // Hospital's overlap probe counts cancelled appointments too, so a slot the agenda shows free can answer «overlaps».
                    // The agenda's own count decides: only another active appointment in the slot is a real overlap for a person to sort out.
                    // An answer with no appointment in it is not a booking: the team checks Hospital (MR-5).
                    reference = created.AppointmentId != Guid.Empty ? created.AppointmentId : throw new JsonException("Hospital no devolvió la cita creada.");
                    overlap = created.Overlaps && await hospital.SlotOccupancyAsync(scope.Id, doctor, start, timeZone, ct) is not (0 or 1);
                }
            }
            // What was done, with whom, and the reference that finds it in Hospital: the CRM history has to stand on its own.
            var with = p.TryGetProperty("doctorName", out var doctorName) ? " con " + doctorName.GetString() : "";
            var done = action == "cancel" ? "Cita cancelada en Hospital a petición del paciente" : $"Cita {(action == "reschedule" ? "reprogramada" : "agendada")} en Hospital: {When(p.GetProperty("startsAt").GetDateTimeOffset())}{with}";
            db.Activities.Add(new Activity { TenantId = scope.Id, ConversationId = conv.Id, ContactId = contact.Id, Kind = "appointment", Actor = "Agente", ActorRole = "agent_ai", Body = $"{done}. Confirmada por el paciente en WhatsApp.{(reference is null ? "" : $" Referencia Hospital: {reference}.")}" }); await db.SaveChangesAsync(ct);
            // Booked, but sharing its slot: recorded like any other, and a person sorts it out with the reference that finds it.
            if (overlap) { await Handoff(conv, $"Cita registrada con un solapamiento. Recepción debe verificar la disponibilidad. Referencia Hospital: {reference}.", ct); return; }
            var booked = action == "cancel" ? "Listo, tu cita quedó cancelada."
                : $"Listo{(registered && FirstName(contact.Name) is { } who ? ", " + who : "")}, {(registered ? "ya te registré y " : "")}tu cita quedó {(action == "reschedule" ? "reprogramada" : "agendada")}:\n*{When(p.GetProperty("startsAt").GetDateTimeOffset())}*\n\n¡Te esperamos!";
            await conversations.Send(conv.Id, booked, "agent", "confirm:" + job.Id, ct: ct, choices: action == "cancel" ? null : new([new("ACTIVAR RECORDATORIOS", "Recordarme la cita")]));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or HospitalIntegrationException or ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // Nothing is retried. An answer from Hospital that cannot be read is as uncertain as a timeout: the proposal is already used (MR-5). With a registration and an appointment in one proposal the team reads how far it got and which hour was wanted.
            // ArgumentException is the Hospital client refusing the appointment itself (duration, doctor, an hour that just passed): the card is used, so a person takes over.
            // A record Hospital created and the agent could not verify is named, so reception reconciles that one instead of registering again.
            var orphan = unverified is { } id ? $" Hospital creó un expediente que no se pudo verificar ni vincular: revísalo antes de registrar de nuevo. Referencia Hospital: {id}." : "";
            // Before any write nothing can have happened in Hospital, so there is nothing to reconcile.
            if (!written) { await Handoff(conv, "No se pudo preparar la operación de agenda; no se envió nada a Hospital.", CancellationToken.None); return; }
            var appointment = reference is { } one && one != Guid.Empty ? $" Referencia Hospital: {one}." : "";
            await Handoff(conv, (registered && wanted is not null ? $"Registro y cita confirmados juntos por WhatsApp: {(contact.PatientId is null ? "el paciente no quedó vinculado" : "el paciente quedó registrado y vinculado")}, y la cita del {wanted} no quedó confirmada. Comprueba el hospital antes de repetir nada."
                : "La operación de agenda necesita conciliación. Comprueba el hospital antes de repetirla.") + appointment + orphan, CancellationToken.None);
        }
    }
    /// <summary>Creates the record in Hospital, verifies it answers with this phone and links it. False when nothing was linked: a possible
    /// duplicate (never forced; reception decides, and reads which hour was wanted) or a contact that already has a record.</summary>
    async Task<bool> Register(Conversation conv, Contact contact, JsonElement p, string? wanted, Job job, string key, CancellationToken ct)
    {
        if (contact.PatientId is not null) { await conversations.Send(conv.Id, "Ya tienes expediente en el hospital.\n¿Para qué día quieres tu cita?", "agent", "confirm:" + job.Id, ct: ct); return false; }
        string Field(string name) => p.GetProperty(name).GetString()!;
        var registered = await hospital.RegisterPatientAsync(scope.Id, new HospitalPatientRegistration(Field("givenNames"), Field("familyNames"), DateOnly.ParseExact(Field("birthDate"), "yyyy-MM-dd"), Field("sex"), contact.Phone, Field("emergencyName"), Field("emergencyRelationship"), Field("emergencyPhone")), key, ct);
        if (!registered.Created || registered.PatientId is not { } patient) { await OfferPerson("Registro por WhatsApp detenido: Hospital encontró un posible expediente existente. Recepción debe revisarlo y vincularlo." + (wanted is null ? "" : $" Quería cita el {wanted}."), "Encontré un expediente que podría ser tuyo.\nVincularlo le toca a recepción.", ct); return false; }
        // Same check as a manual link: the record Hospital created must answer with this conversation's phone.
        unverified = patient; await hospital.GetVerifiedPatientAsync(scope.Id, patient, contact.Phone, ct); unverified = null;
        contact.PatientId = patient; contact.Name = $"{Field("givenNames")} {Field("familyNames")}"; // who the person said they are, confirmed by them
        db.Activities.Add(new Activity { TenantId = scope.Id, ConversationId = conv.Id, ContactId = contact.Id, Kind = "patient_registered", Actor = "Agente", ActorRole = "agent_ai", Body = $"Paciente registrado en Hospital por WhatsApp, con confirmación del paciente. Referencia Hospital: {patient}." }); await db.SaveChangesAsync(ct);
        return true;
    }
    static void RequirePatient(Contact c) { if (c.PatientId is null) throw new ArgumentException("Recepción debe vincular el expediente del paciente"); }
    /// <param name="offerDoctor">The patient asked for a person or a doctor: the doctors the hospital lets patients write to are offered too.</param>
    async Task Handoff(Conversation conv, string reason, CancellationToken ct, bool urgent = false, bool offerDoctor = false)
    {
        using var l = await conversations.Lock(conv.Id, ct); await db.Entry(conv).ReloadAsync(ct); conv.Status = "human"; conv.Summary = reason; conv.Revision++;
        var channel = await db.Channels.SingleAsync(x => x.Id == conv.ChannelId, ct);
        if (channel.DoctorId is { Length: > 0 } doctor && await db.Members.AnyAsync(x => x.Subject == doctor && x.Role == "doctor" && !x.Disabled, ct)) conv.AssignedTo = doctor;
        db.Activities.Add(new Activity { TenantId = scope.Id, ConversationId = conv.Id, ContactId = conv.ContactId, Kind = "handoff", Actor = "Agente", ActorRole = "agent_ai", Body = reason }); await db.SaveChangesAsync(ct);
        if (!channel.Enabled || !Rules.WithinWindow(conv.LastInboundAt, DateTimeOffset.UtcNow)) return;
        var line = await db.Tenants.Where(x => x.Id == scope.Id).Select(x => new { x.EmergencyPhone, x.EmergencyWhatsApp }).SingleAsync(ct);
        var call = line.EmergencyPhone is { Length: > 0 } emergency ? emergency : null;
        // An emergency leads with what to do now; anything else says who answers and when. Both carry the hospital's number.
        await conversations.Send(conv.Id, urgent
            ? $"Si es una emergencia, {(call is null ? "ve a emergencias ahora" : $"llama ya al *{call}* o ve a emergencias")}.\n\nYa avisé al equipo del hospital para que te atienda por aquí."
            : "Le pasé tu consulta al equipo del hospital.\nTe responden por aquí en horario de atención." + (call is null ? "" : $"\n\nSi es una emergencia, llama al *{call}*."), "agent", "handoff:" + activeJobId, ct: ct, choices: Choices.Call(call, config["FRONTEND_URL"]));
        // A message carries one link button. When the hospital says its emergency number answers WhatsApp, the chat is a second one.
        if (urgent && line.EmergencyWhatsApp && Choices.WhatsApp(call) is { } chat)
            await conversations.Send(conv.Id, "También puedes escribirle por WhatsApp a emergencias.", "agent", "handoff-wa:" + activeJobId, ct: ct, choices: chat);
        if (urgent || !offerDoctor || !attention.DoctorChat) return;
        var n = 0;
        foreach (var member in await ContactableDoctors(channel, ct))
            await conversations.Send(conv.Id, $"Si prefieres, puedes escribirle directo a *{member.Name}* por WhatsApp.", "agent", $"handoff-doctor:{activeJobId}:{n++}", ct: ct, choices: Choices.WhatsApp(member.WhatsAppPhone));
    }
    /// <summary>The doctors the hospital lets patients write to. The doctor this number belongs to; else the ones the patient named;
    /// else all of them, up to three: each goes out as its own message, because a message carries one link button.</summary>
    async Task<List<Member>> ContactableDoctors(Channel channel, CancellationToken ct)
    {
        var doctors = (await db.Members.Where(x => x.Role == "doctor" && !x.Disabled && x.WhatsAppPhone != null).OrderBy(x => x.Name).ToListAsync(ct)).Where(x => Choices.WhatsApp(x.WhatsAppPhone) is not null).ToList();
        if (doctors.FirstOrDefault(x => x.Subject == channel.DoctorId) is { } own) return [own];
        var compare = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
        var named = doctors.Where(x => x.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(word => word.Length >= 4 && !word.EndsWith('.')
            && compare.IndexOf(heard, word, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0)).ToList();
        return named.Count > 0 ? named : doctors.Take(3).ToList();
    }
}
