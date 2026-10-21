using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>recordatorios-whatsapp.spec.md, criterios 3 y 6–10: the patient answers the day-before reminder with a tap. Each button
/// carries the id of the reminder that was sent (owner's decision, I-1), so a tap answers exactly that send. «Confirmar» is recorded only
/// against current Hospital facts; «Cambiar fecha» and «Cancelar» never act on their own.</summary>
public sealed class ReminderReplyTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    readonly Guid appointment = Guid.NewGuid();
    // The day after tomorrow at 10:00 in El Salvador, the harness tenant's zone; normalized as the database keeps it.
    readonly DateTimeOffset start = ReminderRules.Instant(new DateTimeOffset(DateTime.UtcNow.Date.AddDays(2).AddHours(16), TimeSpan.Zero));
    AppointmentReminder row = null!;
    static readonly AgentHarness.Fake NoModel = new(_ => throw new InvalidOperationException("The model must not be consulted"));

    public async Task InitializeAsync()
    {
        await h.Start(); await h.Link();
        var tenant = await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id); tenant.RemindersEnabled = true; tenant.ReminderChannelId = h.Conversation.ChannelId;
        h.Contact.ReminderConsentAt = DateTimeOffset.UtcNow;
        row = Reminder(h.Contact.Id, start);
        h.Db.Add(row); await h.Db.SaveChangesAsync();
    }
    public Task DisposeAsync() => h.DisposeAsync().AsTask();

    AppointmentReminder Reminder(Guid contact, DateTimeOffset startsAt, Guid? appointmentId = null, Guid? patient = null, string status = "sent") => new()
    {
        TenantId = h.Scope.Id, ContactId = contact, PatientId = patient ?? h.Contact.PatientId!.Value, ChannelId = h.Conversation.ChannelId,
        AppointmentId = appointmentId ?? appointment, StartsAt = startsAt, DueAt = startsAt.AddDays(-1), Window = "day_before", Status = status, AttemptedAt = status == "pending" ? null : DateTimeOffset.UtcNow,
    };
    object Booked(DateTimeOffset at, string status = "booked", Guid? id = null) => new { appointmentId = id ?? appointment, clinicianId = Guid.NewGuid(), clinicianName = "Dra. Sintética Rivas", placeName = "Consultorio Sintético 3", scheduledStart = at, durationMinutes = 30, status };
    async Task Tap(string body, HttpMessageHandler hospital, Dictionary<string, string?>? extra = null, AgentHarness.Fake? sender = null)
    {
        await h.Say("patient", body);
        await h.Runtime(NoModel, sender ?? h.Sender(), hospital, extra).Run(h.Job, CancellationToken.None);
    }
    async Task<List<Activity>> Notes() => await h.Db.Activities.AsNoTracking().Where(a => a.ConversationId == h.Conversation.Id).ToListAsync();
    async Task<string> Status(Guid? id = null) => (await h.Db.AppointmentReminders.AsNoTracking().SingleAsync(r => r.Id == (id ?? row.Id))).Status;
    async Task Human() { h.Conversation.Status = "human"; await h.Db.SaveChangesAsync(); }

    /// <summary>Sends a pending reminder the way the worker does and returns the three button payloads that went to WhatsApp.</summary>
    async Task<string[]> Dispatch(AppointmentReminder reminder, DateTimeOffset bookedAt, Guid id)
    {
        string[] payloads = [];
        var kapso = new AgentHarness.Fake(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return request.RequestUri!.AbsolutePath.Contains("phone_numbers") ? AgentHarness.Json(new { data = new { business_account_id = "synthetic-waba" } })
                    : AgentHarness.Json(new { data = new[] { new { name = ReminderRules.DayTemplateV4, language = "es", status = "APPROVED", category = "UTILITY", parameter_format = "NAMED", components = new object[] { new { type = "BODY", text = "Cita en {{hospital}} el {{fecha}} a las {{hora}}. BAJA" }, new { type = "BUTTONS", buttons = new[] { new { type = "QUICK_REPLY", text = "Confirmar" }, new { type = "QUICK_REPLY", text = "Cambiar fecha" }, new { type = "QUICK_REPLY", text = "Cancelar" } } } } } } });
            using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            payloads = sent.RootElement.GetProperty("template").GetProperty("components").EnumerateArray().Where(c => c.GetProperty("type").GetString() == "button").Select(c => c.GetProperty("parameters")[0].GetProperty("payload").GetString()!).ToArray();
            return AgentHarness.Json(new { messages = new[] { new { id = "reminder-" + Guid.NewGuid() } } });
        });
        await h.Reminders(kapso, h.Hospital(appointments: [Booked(bookedAt, id: id)]), new() { ["REMINDERS_SEND_ENABLED"] = "true" }).Dispatch(reminder.Id, reminder.DueAt);
        Assert.Equal("sent", await Status(reminder.Id)); Assert.Equal(3, payloads.Length);
        return payloads;
    }

    // Criterio 3 (amended, I-1): the buttons carry the reminder, never the appointment.
    [Fact]
    public async Task ButtonsCarryTheReminderThatWasSent()
    {
        var moved = Guid.NewGuid(); var pending = Reminder(h.Contact.Id, start, moved, status: "pending"); h.Db.Add(pending); await h.Db.SaveChangesAsync();
        Assert.Equal([$"ASISTO {pending.Id}", $"MOVER {pending.Id}", $"CANCELAR {pending.Id}"], await Dispatch(pending, start, moved));
    }

    // Criterio 6: recorded in either conversation state, once.
    [Theory][InlineData("agent")][InlineData("human")]
    public async Task ConfirmingMarksTheReminderOnceInAnyConversationState(string state)
    {
        if (state == "human") await Human();
        var hospital = h.Hospital(appointments: [Booked(start)]);

        await Tap($"ASISTO {row.Id}", hospital);
        await Tap($"ASISTO {row.Id}", hospital);

        Assert.Equal("confirmed", await Status());
        var confirmed = Assert.Single(await Notes(), a => a.Kind == "appointment_confirmed");
        Assert.Equal("El paciente confirmó su asistencia desde el recordatorio.", confirmed.Body);
        Assert.Empty(h.HospitalWrites);
    }

    // I-1 (1): Monday's reminder, tapped after the appointment moved to Thursday, confirms nothing; Thursday's send stays unanswered.
    [Fact]
    public async Task TappingTheOldReminderAfterAMoveConfirmsNothing()
    {
        var moved = Guid.NewGuid(); var thursday = ReminderRules.Instant(start.AddDays(3));
        var monday = Reminder(h.Contact.Id, start, moved, status: "pending"); h.Db.Add(monday); await h.Db.SaveChangesAsync();
        var mondayButtons = await Dispatch(monday, start, moved);
        var later = Reminder(h.Contact.Id, thursday, moved, status: "pending"); later.CreatedAt = DateTimeOffset.UtcNow.AddSeconds(1); h.Db.Add(later); await h.Db.SaveChangesAsync();
        await Dispatch(later, thursday, moved);

        await Tap(mondayButtons[0], h.Hospital(appointments: [Booked(thursday, id: moved)]));

        Assert.Equal("sent", await Status(monday.Id));
        Assert.Equal("sent", await Status(later.Id));
        Assert.Single(await Notes(), a => a.Kind == "appointment_confirmation_rejected");
        Assert.DoesNotContain(await Notes(), a => a.Kind == "appointment_confirmed");
        Assert.Empty(h.Sent);
    }

    // I-1 (2): moved and moved back to the same start, the tap on that send confirms it.
    [Fact]
    public async Task AMoveUndoneLeavesTheSendConfirmable()
    {
        var moved = Guid.NewGuid(); var elsewhere = ReminderRules.Instant(start.AddDays(-1)); var at = start;
        var monday = Reminder(h.Contact.Id, start, moved, status: "pending"); h.Db.Add(monday); await h.Db.SaveChangesAsync();
        var buttons = await Dispatch(monday, start, moved);
        var never = new AgentHarness.Fake(_ => throw new InvalidOperationException("Sync must not send"));
        var hospital = new AgentHarness.Fake(request => Task.FromResult(request.RequestUri!.AbsolutePath.StartsWith("/v1/patients/")
            ? AgentHarness.Json(new { patientId = h.Contact.PatientId, phone = h.Contact.Phone }) : AgentHarness.Json(new { rows = new[] { Booked(at, id: moved) } })));
        at = elsewhere; await h.Reminders(never, hospital).Sync(DateTimeOffset.UtcNow);
        at = start; await h.Reminders(never, hospital).Sync(DateTimeOffset.UtcNow);

        await Tap(buttons[0], h.Hospital(appointments: [Booked(start, id: moved)]));

        Assert.Equal("confirmed", await Status(monday.Id));
        Assert.Single(await Notes(), a => a.Kind == "appointment_confirmed");
    }

    // I-1 (3): a reminder of another contact, patient or tenant is refused.
    [Theory][InlineData("other_contact")][InlineData("other_patient")][InlineData("other_tenant")]
    public async Task AReminderThatIsNotThisPatientsIsRefused(string scenario)
    {
        var target = Guid.NewGuid(); Guid reminder;
        if (scenario == "other_tenant") reminder = await OtherTenantReminder(target);
        else
        {
            var other = new Contact { TenantId = h.Scope.Id, Name = "Otro Sintético", Phone = "50370000111", PhoneHash = Guid.NewGuid().ToString(), PatientId = Guid.NewGuid() };
            h.Db.Add(other);
            var foreign = scenario == "other_contact" ? Reminder(other.Id, start, target, other.PatientId) : Reminder(h.Contact.Id, start, target, Guid.NewGuid());
            h.Db.Add(foreign); await h.Db.SaveChangesAsync(); reminder = foreign.Id;
        }

        // A family phone: Hospital answers for whichever patient is asked with this same number, and has the appointment booked.
        // Only Recepción's own check of contact and patient can refuse.
        var sharedPhone = new AgentHarness.Fake(request => Task.FromResult(request.RequestUri!.AbsolutePath.StartsWith("/v1/patients/")
            ? AgentHarness.Json(new { patientId = Guid.Parse(request.RequestUri.AbsolutePath["/v1/patients/".Length..]), phone = h.Contact.Phone })
            : AgentHarness.Json(new { rows = new[] { Booked(start, id: target) } })));
        await Tap($"ASISTO {reminder}", sharedPhone);

        Assert.Equal("appointment_confirmation_rejected", Assert.Single(await Notes(), a => a.Kind.StartsWith("appointment_confirm")).Kind);
        Assert.Empty(h.Sent);
        await using var any = h.NewContext();
        Assert.Equal("sent", (await any.AppointmentReminders.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == reminder)).Status);
        Assert.Equal("sent", await Status()); // nor this patient's own reminder
    }

    // The tenant filter alone: another tenant's own contact and reminder, asked from this tenant, are not found.
    [Fact]
    public async Task AnotherTenantsReminderIsInvisibleEvenWithItsOwnContact()
    {
        var target = Guid.NewGuid(); var (contact, reminder) = await OtherTenant(target);
        var never = new AgentHarness.Fake(_ => throw new InvalidOperationException("Must not send"));

        var (result, _, _) = await h.Reminders(never, h.Hospital(appointments: [Booked(start, id: target)])).Acknowledge(contact, reminder, h.Conversation.Id, DateTimeOffset.UtcNow);

        Assert.Equal(ReminderAck.Rejected, result);
        await using var any = h.NewContext();
        Assert.Equal("sent", (await any.AppointmentReminders.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == reminder)).Status);
    }

    async Task<Guid> OtherTenantReminder(Guid target) => (await OtherTenant(target)).Reminder;
    async Task<(Guid Contact, Guid Reminder)> OtherTenant(Guid target)
    {
        var connection = Environment.GetEnvironmentVariable("TEST_DATABASE")!;
        var scope = new TenantScope { Id = Guid.NewGuid() };
        await using var other = new CrmDb(new DbContextOptionsBuilder<CrmDb>().UseNpgsql(connection).Options, scope, new EphemeralDataProtectionProvider());
        other.Tenants.Add(new Tenant { Id = scope.Id, Name = "Otro Hospital Sintético" });
        var contact = new Contact { TenantId = scope.Id, Name = "Paciente Sintético", Phone = h.Contact.Phone, PhoneHash = Guid.NewGuid().ToString(), PatientId = h.Contact.PatientId };
        var channel = new Channel { TenantId = scope.Id, Name = "Synthetic", PhoneNumberId = Guid.NewGuid().ToString("N"), Enabled = true };
        other.AddRange(contact, channel);
        var reminder = new AppointmentReminder { TenantId = scope.Id, ContactId = contact.Id, PatientId = contact.PatientId!.Value, ChannelId = channel.Id, AppointmentId = target, StartsAt = start, DueAt = start.AddDays(-1), Window = "day_before", Status = "sent" };
        other.Add(reminder); await other.SaveChangesAsync();
        return (contact.Id, reminder.Id);
    }

    // Criterio 7: never confirmed without current facts; a note for the desk, nothing to the patient.
    [Theory]
    [InlineData("cancelled")][InlineData("moved")][InlineData("past")][InlineData("hospital_down")][InlineData("phone_changed")][InlineData("uncertain")]
    public async Task ConfirmationIsRefusedWithoutCurrentFacts(string scenario)
    {
        var at = start;
        if (scenario == "past") { at = ReminderRules.Instant(DateTimeOffset.UtcNow.AddHours(-2)); row.StartsAt = at; }
        if (scenario == "uncertain") row.Status = "uncertain";
        await h.Db.SaveChangesAsync();
        HttpMessageHandler hospital = scenario switch
        {
            "cancelled" => h.Hospital(appointments: [Booked(start, "cancelled-by-patient")]),
            "moved" => h.Hospital(appointments: [Booked(start.AddHours(2))]),
            "hospital_down" => new AgentHarness.Fake(_ => throw new HttpRequestException("Synthetic Hospital outage")),
            // Hospital now has another phone for the patient: the tap does not come from the patient's number.
            "phone_changed" => new AgentHarness.Fake(request => Task.FromResult(AgentHarness.Json(new { patientId = h.Contact.PatientId, phone = "50370000999" }))),
            _ => h.Hospital(appointments: [Booked(at)]),
        };

        await Tap($"ASISTO {row.Id}", hospital);

        Assert.Equal(scenario == "uncertain" ? "uncertain" : "sent", await Status());
        var notes = await Notes();
        Assert.DoesNotContain(notes, a => a.Kind == "appointment_confirmed");
        var note = Assert.Single(notes, a => a.Kind is "appointment_confirmation_rejected" or "appointment_confirmation_unverified");
        Assert.Equal(scenario == "hospital_down" ? "appointment_confirmation_unverified" : "appointment_confirmation_rejected", note.Kind);
        if (scenario == "hospital_down") Assert.StartsWith("Confirmación recibida, sin verificar con Hospital.", note.Body);
        if (scenario == "uncertain") Assert.Equal("El paciente tocó «Confirmar» en un recordatorio cuya entrega no se confirmó; no se registró. Confirma la cita con el paciente.", note.Body);
        Assert.Empty(h.Sent);
        Assert.Empty(h.HospitalWrites);
    }

    // Criterio 8: thanks only from the active agent with sending on; silence otherwise, and the confirmation still counts.
    [Theory][InlineData("agent")][InlineData("human")][InlineData("paused")]
    public async Task AcceptedConfirmationIsThankedOnlyByTheActiveAgent(string scenario)
    {
        if (scenario == "human") await Human();
        var sender = h.Sender();

        await Tap($"ASISTO {row.Id}", h.Hospital(appointments: [Booked(start)]), scenario == "paused" ? new() { ["SEND_ENABLED"] = "false" } : null, sender);

        Assert.Equal("confirmed", await Status());
        if (scenario != "agent") { Assert.Equal(0, sender.Calls); return; }
        var local = TimeZoneInfo.ConvertTime(start, TimeZoneInfo.FindSystemTimeZoneById("America/El_Salvador"));
        var thanks = Assert.Single(h.Sent);
        Assert.Equal($"Gracias, te esperamos el {local.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} a las {local.ToString("HH:mm", CultureInfo.InvariantCulture)}.", thanks);
        Assert.DoesNotContain("Dra.", thanks); Assert.DoesNotContain("Consultorio", thanks);
    }

    // M-1: a person takes the conversation while Hospital is being re-read: the agent says nothing; the confirmation still counts.
    [Fact]
    public async Task NoThanksOnceAPersonTookTheConversation()
    {
        var sender = h.Sender();
        var hospital = new AgentHarness.Fake(async request =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/v1/patients/")) return AgentHarness.Json(new { patientId = h.Contact.PatientId, phone = h.Contact.Phone });
            await using var desk = h.NewContext();
            var taken = await desk.Conversations.SingleAsync(c => c.Id == h.Conversation.Id); taken.Status = "human"; taken.Revision++; await desk.SaveChangesAsync();
            return AgentHarness.Json(new { rows = new[] { Booked(start) } });
        });

        await Tap($"ASISTO {row.Id}", hospital, sender: sender);

        Assert.Equal("confirmed", await Status());
        Assert.Equal(0, sender.Calls);
    }

    // Criterio 9. The reminder's buttons reuse the agent's two-step flow on the appointment the reminder is about; the deterministic step
    // before the agent must not swallow them. The body is what WhatsAppContent reads from the template tap (criterio 5).
    static string Tapped(string payload) { var message = JsonSerializer.SerializeToElement(new { type = "button", button = new { payload, text = "Cancelar" } }); return WhatsAppContent.Inbound(message, default); }
    [Fact]
    public async Task ReminderButtonsReuseTheTwoStepFlow()
    {
        var target = Guid.NewGuid(); var pending = Reminder(h.Contact.Id, start, target, status: "pending"); h.Db.Add(pending); await h.Db.SaveChangesAsync();
        var buttons = await Dispatch(pending, start, target);
        var doctor = Guid.NewGuid(); var free = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(5).AddHours(15), TimeSpan.Zero);
        var availability = new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = doctor, clinicianName = "Dra. Sintética Rivas", placeName = "Consultorio 1", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = free, durationMinutes = 30, takenBy = 0, offered = true } } } } } } };
        var hospital = h.Hospital(appointments: [Booked(start, id: target)], availability: availability);

        await Tap(Tapped(buttons[1]), hospital);
        Assert.StartsWith($"MOVER {target} ", Assert.Single(AgentHarness.Options(h.Interactive[^1])).Id);
        Assert.Empty(h.HospitalWrites);

        await Tap(Tapped(buttons[2]), hospital);
        Assert.StartsWith("*Cancelar cita:*", h.Sent[^1]);
        Assert.Empty(h.HospitalWrites);

        await Tap(AgentHarness.Options(h.Interactive[^1])[0].Id, hospital);
        Assert.Equal($"/v1/agenda/{target}/cancel", Assert.Single(h.HospitalWrites));
        using var body = JsonDocument.Parse(Assert.Single(h.HospitalBodies));
        Assert.True(body.RootElement.GetProperty("cancelledByPatient").GetBoolean());
        Assert.DoesNotContain(await Notes(), a => a.Kind == "reminder_change_request"); // the agent answered: no note for the desk
    }

    // Criterio 10: with a person, a change or cancel tap only leaves a note for the desk, once.
    [Theory][InlineData("MOVER", "cambiar")][InlineData("CANCELAR", "cancelar")]
    public async Task HumanConversationOnlyNotesChangeRequests(string verb, string said)
    {
        await Human();

        await Tap($"{verb} {row.Id}", h.Hospital(appointments: [Booked(start)]));
        await Tap($"{verb} {row.Id}", h.Hospital(appointments: [Booked(start)]));

        Assert.Empty(h.HospitalWrites);
        Assert.Empty(h.Sent);
        var note = Assert.Single(await Notes(), a => a.Kind == "reminder_change_request");
        Assert.Equal($"El paciente pidió {said} la cita desde el recordatorio.", note.Body);
        Assert.Equal("sent", await Status());
    }

    // A reminder that never reached the patient, or was withdrawn, is not something they can have tapped.
    [Theory][InlineData("pending")][InlineData("cancelled")][InlineData("skipped")]
    public async Task NoChangeNoteForAReminderThatWasNotSent(string status)
    {
        await Human(); row.Status = status; await h.Db.SaveChangesAsync();

        await Tap($"CANCELAR {row.Id}", h.Hospital(appointments: [Booked(start)]));

        Assert.DoesNotContain(await Notes(), a => a.Kind == "reminder_change_request");
    }

    // Criterios 6, 7 y 10 from the real webhook handler, with the template tap of the synthetic fixture (plan §4); and the inbox shows
    // what the patient tapped, never the command.
    [Theory]
    [InlineData("ASISTO", "Confirmar", "booked", "appointment_confirmed", "Tocó: Confirmar")]
    [InlineData("ASISTO", "Confirmar", "cancelled-by-patient", "appointment_confirmation_rejected", "Tocó: Confirmar")]
    [InlineData("CANCELAR", "Cancelar", "booked", "reminder_change_request", "Tocó: Cancelar")]
    [InlineData("MOVER", "Cambiar fecha", "booked", "reminder_change_request", "Tocó: Cambiar fecha")]
    public async Task TapsArriveThroughTheWebhook(string verb, string text, string hospitalStatus, string kind, string shown)
    {
        await Human();
        var job = await Webhook($"{verb} {row.Id}", text);

        h.Db.ChangeTracker.Clear();
        await h.Runtime(NoModel, h.Sender(), h.Hospital(appointments: [Booked(start, hospitalStatus)])).Run(job, CancellationToken.None);

        Assert.Single(await Notes(), a => a.Kind == kind);
        Assert.Equal(kind == "appointment_confirmed" ? "confirmed" : "sent", await Status());
        Assert.Empty(h.HospitalWrites); Assert.Empty(h.Sent);
        await using var inbox = h.NewContext();
        Assert.Equal(shown, (await inbox.Conversations.AsNoTracking().SingleAsync(c => c.Id == h.Conversation.Id)).LastMessage);
        var messages = await WhatsAppEndpoints.Messages(h.Conversation.Id, inbox);
        Assert.Equal(shown, messages!.Single(m => m.Type == "button").Body); // the tap, whatever its second-precision timestamp sorts it to
        Assert.DoesNotContain(messages!, m => m.Body.Contains(row.Id.ToString()));
        Assert.Equal($"{verb} {row.Id}", (await inbox.Messages.AsNoTracking().SingleAsync(m => m.Type == "button")).Body); // the agent still reads the command
    }

    async Task<Job> Webhook(string payload, string text)
    {
        const string hashKey = "synthetic-phone-hash-key", secret = "synthetic-webhook-secret";
        h.Contact.PhoneHash = Rules.PhoneHash(h.Contact.Phone, hashKey); await h.Db.SaveChangesAsync();
        var channel = await h.Db.Channels.AsNoTracking().SingleAsync(c => c.Id == h.Conversation.ChannelId);
        var id = "wamid.synthetic-" + Guid.NewGuid().ToString("N");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            phone_number_id = channel.PhoneNumberId,
            conversation = new { id = "conv-synthetic", phone_number = h.Contact.Phone, contact_name = "Paciente Sintético" },
            message = new { id, from = h.Contact.Phone, timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), type = "button", context = new { from = "50300000000", id = "wamid.synthetic-reminder" }, button = new { payload, text }, kapso = new { direction = "inbound", status = "received", origin = "cloud_api", has_media = false, content = text } },
        });
        var ctx = new DefaultHttpContext(); ctx.Request.Body = new MemoryStream(body); ctx.Request.ContentLength = body.Length;
        ctx.Request.Headers["X-Webhook-Signature"] = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();
        ctx.Request.Headers["X-Webhook-Event"] = "whatsapp.message.received"; ctx.Request.Headers["X-Idempotency-Key"] = id;
        await using (var webhook = h.NewContext())
            await WhatsAppEndpoints.Receive(ctx, webhook, h.Scope, h.Config(new() { ["KAPSO_WEBHOOK_SECRET"] = secret, ["PHONE_HASH_KEY"] = hashKey }), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await using var read = h.NewContext();
        return await read.Jobs.AsNoTracking().SingleAsync(j => j.Key == "agent:" + id);
    }
}
