using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
namespace Recepcion;

/// <summary>What the conversation waits on: the agent's held questions and cards (MR-40), a tapped reminder, and the team after a handoff.</summary>
public sealed partial class AgentRuntime
{
    /// <summary>Deploy transition of MR-40: a question the agent sent before PendingActions existed has none. Within its 24 hours, a message the agent
    /// sent before the cut (AGENT_LEGACY_QUESTIONS_BEFORE, ISO; set it to the deploy time) is read by its canonical words, as before.</summary>
    // ponytail: retire this and the setting after 2026-11-17; a day after the deploy nothing can still match it.
    async Task<string?> LegacyQuestion(Message? asked, CancellationToken ct)
    {
        var cut = LegacyQuestionsBefore(config);
        if (asked is not { Sender: "agent" } || asked.CreatedAt >= cut || asked.CreatedAt < DateTimeOffset.UtcNow - QuestionLife || await db.PendingActions.AnyAsync(x => x.PromptMessageId == asked.Id, ct)) return null;
        return asked.Body.Contains(AgentGuard.EmergencyQuestion) ? "emergency" : asked.Body.Contains(AgentGuard.PersonQuestion) ? "person"
            : asked.Body.Contains(EmergencyContact, StringComparison.OrdinalIgnoreCase) ? "emergency-contact" : null;
    }
    const string EmergencyContact = "contacto de emergencia";
    /// <summary>When this change was committed (UTC); the deploy sets the real cut.</summary>
    static readonly DateTimeOffset LegacyCut = new(2026, 10, 18, 1, 30, 0, TimeSpan.Zero);
    /// <summary>The cut of the deploy transition. Unset, the commit's; set, an ISO-8601 time with its zone, or the API does not start
    /// (Hosting.AddRecepcionServices): read as the default by mistake, the old version's questions would go unanswered.</summary>
    public static DateTimeOffset LegacyQuestionsBefore(IConfiguration config) =>
        config["AGENT_LEGACY_QUESTIONS_BEFORE"] is not { Length: > 0 } value ? LegacyCut
        : Regex.IsMatch(value.Trim(), @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$") && DateTimeOffset.TryParse(value.Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var set) ? set
        : throw new InvalidOperationException("AGENT_LEGACY_QUESTIONS_BEFORE must be an ISO-8601 time with its zone, e.g. 2026-10-18T09:00:00-06:00 (docs/deployment.md)");
    /// <summary>Something the agent now waits on in this conversation; it is matched to the next message the agent sends (<see cref="Reply"/>).</summary>
    PendingAction Hold(string kind, string code, string body, TimeSpan life)
    {
        var pending = new PendingAction { TenantId = scope.Id, ConversationId = conv.Id, Kind = kind, Code = code, Body = body, ExpiresAt = DateTimeOffset.UtcNow + life };
        db.PendingActions.Add(pending); held.Add(pending); return pending;
    }
    /// <summary>What this conversation still waits on, of one kind: not consumed and not expired.</summary>
    IQueryable<PendingAction> Open(string kind) => db.PendingActions.Where(x => x.ConversationId == conv.Id && x.Kind == kind && x.ConsumedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow);
    /// <summary>Used once: what it held goes with it, and that is saved before anything is done with it (INV-6).</summary>
    async Task Consume(PendingAction pending, CancellationToken ct) { pending.ConsumedAt = DateTimeOffset.UtcNow; pending.Body = ""; await db.SaveChangesAsync(ct); }
    /// <summary>The agent's message of this turn. What was held for it is now waiting on this message.</summary>
    async Task<Message> Reply(string body, CancellationToken ct, Choices? choices = null)
    {
        var sent = await conversations.Send(conv.Id, body, "agent", "agent:" + job.Id, ct: ct, choices: choices);
        if (held.Count > 0) { foreach (var pending in held) pending.PromptMessageId = sent.Id; held.Clear(); await db.SaveChangesAsync(CancellationToken.None); }
        return sent;
    }

    /// <summary>A tap on a day-before reminder (recordatorios-whatsapp.spec.md, criterios 6, 8, 10; I-1: the payload is the reminder's id).
    /// Runs before every gate of the agent, so it counts with the agent or with a person, and reads this job's own message, so a «gracias»
    /// typed right after does not hide it. «Confirmar» ends here: the model never sees the command, and the thanks goes only if the agent
    /// still owns the conversation after the Hospital re-read. «Cambiar fecha» / «Cancelar» with nobody automatic to answer leave one
    /// note per send, and only for a reminder that reached the patient.</summary>
    async Task<bool> ReminderReply(Tenant tenant, CancellationToken ct)
    {
        if (!job.Key.StartsWith("agent:", StringComparison.Ordinal)) return false;
        var externalId = job.Key[6..];
        var body = await db.Messages.Where(x => x.ConversationId == conv.Id && x.ExternalId == externalId && x.Sender == "patient").Select(x => x.Body).FirstOrDefaultAsync(ct);
        if (body is null) return false;
        var agentAnswers = kapso.CanSend(false) && tenant.AgentEnabled && channel.Enabled && conv.State == "open" && conv.Status == "agent";
        if (ReminderRules.Acknowledgement(body) is { } reminder)
        {
            hospital.CallAs($"Recepcion-Recordatorios/1.0 (conversacion {conv.Id}; trabajo {job.Id})", job.Id);
            var (result, date, time) = await new AppointmentReminderService(db, scope, hospital, kapso, conversations, config).Acknowledge(conv.ContactId, reminder, conv.Id, DateTimeOffset.UtcNow, ct);
            if (result == ReminderAck.Confirmed && agentAnswers)
            {
                // A person may have taken the conversation while Hospital was being read: as in Confirm, the agent speaks only if it still owns it.
                using var lease = await conversations.Lock(conv.Id, ct); await db.Entry(conv).ReloadAsync(ct);
                if (await Active(conv.Id, conv.Revision, ct)) await conversations.Send(conv.Id, $"Gracias, te esperamos el {date} a las {time}.", "agent", "asisto:" + job.Id, ct: ct);
            }
            return true;
        }
        if (!agentAnswers && await TappedReminder(body, ct) is { } tapped && tapped.Row.Status is "sent" or "confirmed" or "uncertain")
        {
            var note = $"El paciente pidió {(tapped.Verb == "MOVER" ? "cambiar" : "cancelar")} la cita desde el recordatorio.";
            var since = tapped.Row.AttemptedAt ?? tapped.Row.CreatedAt;
            // MessageId is the reminder that was tapped: «Mañana» marks that reminder, not every one of the conversation.
            if (!(await db.Activities.Where(x => x.ConversationId == conv.Id && x.Kind == "reminder_change_request" && x.MessageId == tapped.Row.MessageId && x.CreatedAt >= since).Select(x => x.Body).ToListAsync(ct)).Contains(note))
            {
                db.Activities.Add(new() { TenantId = scope.Id, ContactId = conv.ContactId, ConversationId = conv.Id, MessageId = tapped.Row.MessageId, Actor = "Recordatorios", ActorRole = "system", Kind = "reminder_change_request", Body = note });
                await db.SaveChangesAsync(ct);
            }
        }
        return false;
    }
    /// <summary>«Cambiar fecha» / «Cancelar» of the day before, or «Necesito reprogramar» / «Cancelar cita» of the hour before (S5, criterio 50),
    /// of a reminder sent to this contact: the verb and that reminder.</summary>
    async Task<(string Verb, AppointmentReminder Row)?> TappedReminder(string body, CancellationToken ct) =>
        AgentGuard.AppointmentChoice(body) is { Verb: "MOVER" or "CANCELAR", StartsAt: null } change
        && await db.AppointmentReminders.AsNoTracking().SingleOrDefaultAsync(r => r.Id == change.Appointment && r.ContactId == conv.ContactId, ct) is { } row
            ? (change.Verb, row) : null;
    /// <summary>When the agent handed this conversation to the team, if no person has answered or acted on it since. A conversation
    /// the team took by itself, or one that stopped on an error, is theirs alone.</summary>
    async Task<DateTimeOffset?> HandedOverUnanswered(CancellationToken ct)
    {
        var last = await db.Activities.Where(x => x.ConversationId == conv.Id && (x.Kind == "handoff" || x.Kind == "error" || x.Kind == "delivery" || x.ActorSubject != null)).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (last is not { Kind: "handoff", ActorRole: "agent_ai" }) return null;
        return await db.Messages.AnyAsync(x => x.ConversationId == conv.Id && x.Sender == "human" && x.CreatedAt > last.CreatedAt, ct) ? null : last.CreatedAt;
    }
    /// <summary>The patient wrote again and nobody has answered yet. Said after ten minutes and at most every two hours; the team sees the conversation rise.</summary>
    async Task StillWaiting(DateTimeOffset since, string? emergencyPhone, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (since > now.AddMinutes(-10) || await db.Activities.AnyAsync(x => x.ConversationId == conv.Id && x.Kind == "waiting_notice" && x.CreatedAt > since && x.CreatedAt > now.AddHours(-2), ct)) return;
        using (await conversations.Lock(conv.Id, ct))
        {
            await db.Entry(conv).ReloadAsync(ct); if (conv.Priority is "low" or "normal") { conv.Priority = "high"; conv.Revision++; }
            db.Activities.Add(new Activity { TenantId = scope.Id, ConversationId = conv.Id, ContactId = contact.Id, Kind = "waiting_notice", Actor = "Agente", ActorRole = "agent_ai", Body = "El paciente volvió a escribir y nadie le ha respondido desde la derivación. Se le avisó y la conversación subió de prioridad." });
            await db.SaveChangesAsync(ct);
        }
        // What this hospital's menu shows, and nothing it hid.
        List<Choice> taps = [.. new Choice[] { new("AGENDAR", "Agendar cita"), new("MISCITAS", "Mis citas"), new("RECETA", "Mi receta") }.Where(option => attention.Shown.Contains(option.Id))];
        var text = "Tu consulta sigue con el equipo del hospital.\nTodavía no han podido responderte; lo harán por aquí en horario de atención."
            + (emergencyPhone is { Length: > 0 } ? $"\n\nSi es una emergencia, llama al *{emergencyPhone}*." : "") + (contact.PatientId is null || taps.Count == 0 ? "" : "\n\nMientras tanto, con esto sí te ayudo yo:");
        await conversations.Send(conv.Id, text, "agent", "agent:" + job.Id, ct: ct, choices: contact.PatientId is null ? Choices.Call(emergencyPhone, config["FRONTEND_URL"]) : taps.Count > 0 ? new Choices(taps) : null);
    }
    /// <summary>The patient decides. The agent says what it could not do and offers a person with a button; the reason is kept for the team,
    /// never shown in the chat.</summary>
    async Task OfferPerson(string reason, string message, CancellationToken ct)
    {
        // The reason goes with the question: a «sí» to it hands off with what the team needs to know.
        db.Activities.Add(new Activity { TenantId = scope.Id, ConversationId = conv.Id, ContactId = contact.Id, Kind = "handoff_offer", Actor = "Agente", ActorRole = "agent_ai", Body = reason }); Hold("question", "person", reason, QuestionLife); await db.SaveChangesAsync(ct);
        await Reply(message + "\n\n" + AgentGuard.PersonQuestion, ct, new([new("PERSONA", "Hablar con persona"), new("MENU", "Seguir aquí")]));
    }
}
