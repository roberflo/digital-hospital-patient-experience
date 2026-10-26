using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Recepcion.Integrations;
namespace Recepcion;

/// <summary>The tools the model may call, and the guard that runs around each call.</summary>
public sealed partial class AgentRuntime
{
    /// <summary>Runs around every tool call: budget, pause checks, audit trail and grounding.</summary>
    async ValueTask<object?> GuardTool(AIAgent agent, FunctionInvocationContext context, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next, CancellationToken ct)
    {
        if (++calls > AgentGuard.ToolBudget || !await Active(conv.Id, revision, ct)) { stopped = calls <= AgentGuard.ToolBudget; context.Terminate = true; return Result(new { error = "Atención automática pausada" }); }
        if (refill && context.Function.Name is "send_latest_prescription" or "send_prescription") return Result(new { error = "Piden una receta nueva o un resurtido: no entregues la anterior. Explica que una receta nueva la decide el doctor y usa handoff." });
        if (forOther && context.Function.Name is "start_registration" or "propose_registration" or "propose_action") return Result(new { error = "La petición es para otra persona. Desde este número sólo se registra y se agenda a quien escribe: explícalo y di que esa persona escriba desde su propio WhatsApp o pase por recepción." });
        // hospital_contact and hospital_disciplines stay open with a foreign identifier, like the agenda: they take no argument and return what
        // the hospital says of itself to anyone, so there is nobody's data to select with them.
        if (foreignId && context.Function.Name is not ("handoff" or "record_note" or "hospital_availability" or "hospital_contact" or "hospital_disciplines")) return Result(new { error = "El mensaje trae un identificador ajeno. No consultes datos: explica que sólo atiendes al titular de esta conversación." });
        object? result;
        try { result = await next(context, ct); }
        catch (Exception ex) when (ex is HttpRequestException or ArgumentException or JsonException or KeyNotFoundException or FormatException or HospitalIntegrationException) { result = Result(new { error = "No se pudo completar la operación. Deriva a recepción para verificar." }); }
        catch (Exception ex) when (ex is not OperationCanceledException) { fault = ExceptionDispatchInfo.Capture(ex); context.Terminate = true; return Result(new { error = "Operación interrumpida" }); }
        var failed = result is JsonElement { ValueKind: JsonValueKind.Object } element && element.TryGetProperty("error", out _);
        db.Activities.Add(new Activity { TenantId = scope.Id, ConversationId = conv.Id, ContactId = contact.Id, Kind = "agent_tool", Actor = "Agente", ActorRole = "agent_ai", Body = $"Herramienta: {context.Function.Name}. Resultado: {(failed ? "requiere revisión" : "completado")}." }); await db.SaveChangesAsync(ct);
        if (result is JsonElement json) grounding.AppendLine(json.GetRawText());
        if (conv.Status != "agent" || conv.State != "open" || offer is not null || startIntake) context.Terminate = true;
        return result;
    }
    static JsonElement Result(object value) => JsonSerializer.SerializeToElement(value, Json);
    /// <summary>Both registration tools start the form (docs/agent-evidence.md): it takes only what the patient wrote in this conversation, so a
    /// value the model gives and the patient never wrote counts as not given and is asked. Nothing of it is kept.</summary>
    JsonElement StartIntake(params string?[] given)
    {
        if (contact.PatientId is not null) throw new ArgumentException("El contacto ya tiene expediente");
        var said = session.Where(x => x.Sender == "patient").Select(x => x.Body).ToList(); var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        startIntake = true; known = given.Select((value, field) => !string.IsNullOrWhiteSpace(value) && Intake.Wrote(field, value, said, today) ? value : null).ToArray();
        return Result(new { started = true, instruction = "El sistema pregunta al paciente sólo los datos que falten, uno por uno. No escribas nada más." });
    }

    /// <summary>The only actions the model can take. Patient, phone and tenant come from the
    /// conversation, never from an argument.</summary>
    IList<AITool> Tools() => [
        AIFunctionFactory.Create(async ([Description("Motivo de la derivación")] string reason, [Description("true sólo si el paciente ya respondió que sí es una emergencia; nunca por síntomas")] bool urgent = false, CancellationToken ct = default) =>
        {
            // Urgent goes at once, but only on the patient's word: symptoms alone are not an emergency until the patient has been asked.
            // Anything else is offered: the patient decides with a button whether to go to a person.
            var why = Rules.Required(reason, 1000);
            if (urgent && !emergencyAsked && attention.Symptoms == "ask") { symptoms = true; return Result(new { transferred = false, instruction = "No se derivó: no supongas una emergencia. Atiende al paciente: dile en una frase que lo sientes y ofrécele una cita con hospital_availability." + (emergencyLine ? " El sistema añade la pregunta de si es una emergencia; no la escribas tú." : " No le preguntes si es una emergencia.") }); }
            if (urgent && emergencyAsked) { await Handoff(conv, why, ct, true); return Result(new { transferred = true }); }
            offer = why; return Result(new { offered = true, instruction = "El sistema ya le pregunta al paciente si quiere pasar con una persona. No escribas nada más." });
        }, "handoff", "Pasar con una persona del hospital: de inmediato si el paciente ya dijo que es una emergencia; si no, el sistema se lo ofrece al paciente con un botón"),
        AIFunctionFactory.Create(async (string note, CancellationToken ct) => { db.Activities.Add(new Activity { TenantId = scope.Id, ContactId = contact.Id, ConversationId = conv.Id, Kind = "note", Actor = "Agente", ActorRole = "agent_ai", Body = Rules.Required(note, 2000) }); await db.SaveChangesAsync(ct); noted = true; return Result(new { saved = true }); }, "record_note", "Guardar nota útil del seguimiento"),
        AIFunctionFactory.Create(async ([Description("trato, doctor_preferido u horario_preferido")] string key, [Description("Lo que dijo el paciente, en pocas palabras. Vacío para olvidarlo")] string value, CancellationToken ct) =>
        {
            if (value.Trim().Length > 0 && !AgentMemory.Accepts(key, value)) return Result(new { error = "Eso no se guarda: sólo cómo quiere que le llamen, su doctor preferido o su horario preferido, y nunca datos de salud. Dilo en una frase y sigue." });
            var kept = await db.ContactMemories.SingleOrDefaultAsync(x => x.ContactId == contact.Id && x.Key == key, ct);
            if (value.Trim().Length == 0) { if (kept is not null) db.ContactMemories.Remove(kept); }
            else if (kept is null) db.ContactMemories.Add(new ContactMemory { TenantId = scope.Id, ContactId = contact.Id, Key = key, Value = value.Trim() });
            else { kept.Value = value.Trim(); kept.UpdatedAt = DateTimeOffset.UtcNow; }
            await db.SaveChangesAsync(ct); return Result(new { saved = true });
        }, "remember", "Guardar para próximas conversaciones una preferencia que el paciente dijo de sí mismo. Nunca datos de salud"),
        AIFunctionFactory.Create(async ([Description("Palabras clave de lo que se busca")] string words, CancellationToken ct) =>
        {
            // Every conversation of this contact, on any channel, before the one in progress. Bodies are encrypted: they are filtered here, not in SQL.
            var since = session[0].CreatedAt;
            var older = await db.Messages.Where(m => m.CreatedAt < since && db.Conversations.Any(c => c.Id == m.ConversationId && c.ContactId == contact.Id)).OrderByDescending(m => m.CreatedAt).Take(AgentMemory.RecallScan).ToListAsync(ct);
            return Result(new { found = AgentMemory.Search(older, words, zone) });
        }, "recall", "Buscar en mensajes de días pasados de este paciente algo que no está en la conversación en curso"),
        AIFunctionFactory.Create(async ([Description("YYYY-MM-DD")] string date, string? doctorId = null, [Description("Sólo si el paciente dijo que la cita es para esto: procedure, results o paperwork, como en propose_action. Para una consulta no lo pongas")] string? kind = null, CancellationToken ct = default) =>
        {
            // The patient usually taps one of these hours, and a tapped hour is proposed without the model: what the appointment is for is kept here for it.
            if (kind is "procedure" or "results" or "paperwork") { Hold("visit_kind", kind, "", ProposalLife); await db.SaveChangesAsync(ct); }
            return await Availability(date, doctorId, ct, labelled: true);
        }, "hospital_availability", "Consultar horarios libres desde una fecha: devuelve los de esa fecha o, si no tiene, los del primer día siguiente con agenda"),
        AIFunctionFactory.Create(async ([Description("YYYY-MM-DD")] string date, CancellationToken ct) => 
        {
            RequirePatient(contact); var rows = await hospital.GetPatientAppointmentsAsync(scope.Id, contact.PatientId!.Value, contact.Phone, DateOnly.Parse(date), ct);
            // Same labelling as the free slots: the hospital's local date, weekday and clock, whatever offset Hospital answered in.
            return Result(rows.EnumerateArray().Select(row => { var local = TimeZoneInfo.ConvertTime(row.GetProperty("startsAt").GetDateTimeOffset(), zone); var minutes = row.GetProperty("durationMinutes").GetInt32(); return new { appointmentId = row.GetProperty("appointmentId").GetGuid(), date = local.ToString("yyyy-MM-dd"), weekday = Weekdays[(int)local.DayOfWeek], time = local.ToString("HH:mm"), until = local.AddMinutes(minutes).ToString("HH:mm"), durationMinutes = minutes, doctor = row.GetProperty("doctor").GetString(), status = row.GetProperty("status").GetString() }; }).ToList());
        }, "my_appointments", "Consultar las citas de este paciente para una fecha; usar antes de mover o cancelar"),
        AIFunctionFactory.Create(async (string? cursor = null, CancellationToken ct = default) => { RequirePatient(contact); return Result(await hospital.ListIssuedPrescriptionsAsync(scope.Id, contact.PatientId!.Value, contact.Phone, cursor, ct)); }, "my_prescriptions", "Listar recetas firmadas de este paciente; usar nextCursor para otra página"),
        AIFunctionFactory.Create(async (string prescriptionId, CancellationToken ct) => { RequirePatient(contact); return Result(await hospital.GetIssuedPrescriptionAsync(scope.Id, contact.PatientId!.Value, contact.Phone, Guid.Parse(prescriptionId), ct)); }, "get_prescription", "Consultar indicaciones de receta firmada"),
        AIFunctionFactory.Create((CancellationToken ct) => SendPrescription(null, ct), "send_latest_prescription", "Enviar la última receta firmada de este paciente cuando pide su receta sin nombrar una en particular; el servidor elige la firma más reciente"),
        AIFunctionFactory.Create((string prescriptionId, CancellationToken ct) => SendPrescription(Guid.Parse(prescriptionId), ct), "send_prescription", "Entregar el PDF firmado de una receta específica que el paciente nombró; si no nombró una, usa send_latest_prescription"),
        AIFunctionFactory.Create(([Description("create, reschedule o cancel")] string action, string? doctorId = null, string? appointmentId = null, [Description("ISO8601 con zona")] string? startsAt = null, int? durationMinutes = null, [Description("Sólo al crear y sólo si el paciente dijo que la cita es para esto: procedure (un procedimiento ya indicado), results (recoger resultados) o paperwork (un trámite). Para una consulta no lo pongas")] string? kind = null, CancellationToken ct = default) => Propose(action, doctorId, appointmentId, startsAt, durationMinutes, ct, kind: kind), "propose_action", "Proponer crear, mover o cancelar cita. Requiere confirmación del paciente"),
        AIFunctionFactory.Create((string givenNames, string familyNames, [Description("YYYY-MM-DD")] string birthDate, [Description("female o male")] string sex, string emergencyContactName, [Description("Parentesco con el paciente")] string emergencyContactRelationship, string emergencyContactPhone, CancellationToken ct) => StartIntake(givenNames, familyNames, birthDate, sex, emergencyContactName, emergencyContactRelationship, emergencyContactPhone), "propose_registration", "Proponer el registro como paciente de quien escribe, cuando no tiene expediente. Requiere confirmación del paciente"),
        AIFunctionFactory.Create((string? givenNames = null, string? familyNames = null, [Description("Como la dijo el paciente")] string? birthDate = null, [Description("femenino o masculino")] string? sex = null, string? emergencyContactName = null, string? emergencyContactRelationship = null, string? emergencyContactPhone = null) =>
            StartIntake(givenNames, familyNames, birthDate, sex, emergencyContactName, emergencyContactRelationship, emergencyContactPhone), "start_registration", "Registrar a quien escribe cuando no tiene expediente. Pasa los datos que el paciente YA dijo, tal cual los dijo, sin completar ninguno; el sistema pide los que falten"),
        AIFunctionFactory.Create(async ([Description("Nombre o palabra del servicio; vacío para ver qué hay")] string? words = null, CancellationToken ct = default) =>
        {
            // Hospital's catalogue of active services. Whether a service exists is always answered; what it costs, only when the hospital turned prices on.
            // Names, kind and discipline only: ids, codes and versions are the staff screen's business.
            var asked = words?.Trim() ?? ""; var priced = attention.Prices == "on"; HospitalCommercialPage<HospitalService> page;
            try
            {
                page = await hospital.Services(scope.Id, asked, 1, ct);
                // Hospital looks for the whole phrase inside the name, so «limpieza de dientes» misses «Limpieza dental»: asked once more with its longest word.
                if (page.Items.Count == 0 && asked.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } parts && parts.MaxBy(part => part.Length) is { Length: >= 4 } longest) page = await hospital.Services(scope.Id, longest, 1, ct);
            }
            // The catalogue adds to the guide, it does not replace it: with Hospital down the question the guide answers is still answered, not handed to a person.
            catch (Exception ex) when (ex is HttpRequestException or HospitalIntegrationException) { return Result(new { available = false, instruction = "No se pudo consultar el catálogo. Responde sólo con lo que diga la guía; si la guía tampoco lo dice, di que no lo tienes y ofrece pasar la consulta a recepción." }); }
            if (page.Items.Count == 0) return Result(new { found = false, instruction = "No consta en el catálogo. Si la guía lo dice, responde con la guía. Si tampoco consta ahí, di que no consta, sin afirmar que el hospital no lo ofrece, y ofrece pasar la consulta a recepción." });
            var services = new JsonArray();
            foreach (var service in page.Items.Take(30))
            {
                var item = new JsonObject { ["name"] = service.Name, ["kind"] = service.Kind, ["discipline"] = service.Discipline };
                // A price of 0 is a service nobody has priced yet (the dental starter catalogue ships that way), never a free one.
                // Written as «25.00 USD» so the money guard recognises the figure the model may repeat. The guard only knows dollars:
                // a price in another currency would go out unchecked, so it is left to reception.
                if (priced && service.Price > 0 && service.Currency == "USD") item["listPrice"] = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{service.Price:0.00} {service.Currency}");
                services.Add(item);
            }
            var result = new JsonObject { ["services"] = services };
            if (page.Total > services.Count) result["more"] = true;
            if (!priced) result["instruction"] = "Este hospital no da por WhatsApp los precios de su catálogo. Si la guía dice el precio, dilo tal como está en la guía; si no, el precio lo confirma recepción.";
            else if (services.Any(s => s!["listPrice"] is null)) result["instruction"] = "Un servicio que aquí viene sin precio no lo tiene publicado: nómbralo y di que su precio lo confirma recepción, salvo que la guía lo diga. Nunca escribas gratis, gratuito ni sin costo, ni siquiera negados.";
            return Result(result);
        }, "hospital_services", "Consultar qué servicios ofrece el hospital según su catálogo. Usar antes de responder si el hospital ofrece algo o cuánto cuesta"),
        AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            // What the hospital itself published with its public booking link: public by its own decision, so it may be said to whoever asks.
            // A Hospital without the route, or without a published link, answers nothing and the guide is the source, as it always was.
            var published = await hospital.ClinicContact(scope.Id, ct); var address = published?.PublicAddress?.Trim(); var phone = published?.PublicPhone?.Trim();
            if (string.IsNullOrEmpty(address) && string.IsNullOrEmpty(phone)) return Result(new { found = false, instruction = "El hospital no tiene publicados aquí su dirección ni su teléfono. Si la guía los dice, responde con la guía; si tampoco constan ahí, di que no constan y ofrece pasar la consulta a recepción." });
            return Result(new { address = string.IsNullOrEmpty(address) ? null : address, phone = string.IsNullOrEmpty(phone) ? null : phone, instruction = "Di estos datos tal cual, sin completarlos. Lo que venga vacío, búscalo en la guía; si tampoco consta ahí, di que no consta." });
        }, "hospital_contact", "Consultar la dirección y el teléfono que el hospital publicó. Usar antes de responder dónde está el hospital o a qué número llamar"),
        AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            // «¿Tienen dentista?» is a question, not a booking. It is answered from Hospital's roll alone: the agenda is not read, so no list of hours,
            // no «hay espacio» and no emergency question ride on the answer, and a dentist with no free hour today is still a dentist the hospital has.
            var labels = (await hospital.ClinicianDisciplines(scope.Id, ct)).Values.Select(DisciplineLabel).OfType<string>().Distinct().Order().ToList();
            return labels.Count == 0
                ? Result(new { found = false, instruction = "Hospital no informa aquí las disciplinas de sus profesionales. Si la guía lo dice, responde con la guía; si tampoco consta ahí, di que no consta, sin afirmar que no hay, y ofrece pasar la consulta a recepción." })
                : Result(new { disciplines = labels, instruction = "Responde sólo por esta lista. Una disciplina que no aparece aquí ni en la guía no consta: dilo así, nunca que no hay, y ofrece pasar la consulta a recepción." });
        }, "hospital_disciplines", "Consultar qué disciplinas tienen los profesionales del hospital (medicina, odontología, nutrición). Usar para responder si hay dentista, nutricionista o médico; no consulta horarios"),
        // Registered only for a hospital that says its prices: with them off the model cannot even ask.
        .. attention.Prices == "on" ? new AITool[] { AIFunctionFactory.Create(async ([Description("Nombre de la empresa, como lo dijo el paciente")] string company, CancellationToken ct) =>
        {
            // Asked by name, never listed. Who the hospital's corporate clients are is not the patient's to learn, so two things hold whatever the model sends:
            // the name is one the patient wrote (a model walking «S.A.», «Banco», «Grupo» gets nothing), and the answer is one company or none.
            var asked = company.Trim(); if (asked.Length < 3) throw new ArgumentException("El nombre de la empresa es demasiado corto");
            var name = ActivityFeed.SearchText(asked); // without case or accents: «compania» is «Compañía»
            // ponytail: «wrote» is the patient's last three messages (heard); a company named earlier than that is asked for again.
            if (!ActivityFeed.SearchText(heard).Contains(name)) return Result(new { found = false, instruction = "El paciente no escribió ese nombre. Pregúntale cómo se llama su empresa y consulta con el nombre que te dé; no pruebes nombres." });
            // Hospital's search is by name; the same match is made here so that nothing else it might one day search by (a tax id, an email) becomes an answer.
            var eligible = (await hospital.Companies(scope.Id, asked, 1, ct)).Items.Where(c => c.Active && c.Agreement is { Active: true } && ActivityFeed.SearchText(c.Name).Contains(name)).ToList();
            // «Empresa Sintética» when there is also «Empresa Sintética Dos»: the one called exactly that is the one asked for.
            if (eligible.Count > 1 && eligible.Where(c => ActivityFeed.SearchText(c.Name) == name).ToList() is [var exact]) eligible = [exact];
            if (eligible.Count == 0) return Result(new { found = false, instruction = "No consta un convenio con ese nombre. Dilo así, sin afirmar que no existe; recepción lo confirma." });
            if (eligible.Count > 1) return Result(new { found = false, instruction = "Ese nombre no basta para saber de qué empresa se trata. Pídele al paciente el nombre completo de su empresa; no menciones ninguna." });
            // Only the name and the percentage leave: never the tax id, the email, the phone or an id.
            return Result(new { company = eligible[0].Name, agreement = eligible[0].Agreement!.Name, discount = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{eligible[0].Agreement!.DiscountPercent:0.##} %") });
        }, "hospital_agreement", "Consultar si la empresa del paciente tiene convenio con el hospital y su porcentaje de descuento. Sólo con el nombre que el paciente escribió") } : []
    ];
}
