using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>docs/reception-agent.md, criterios 24–30: the patient taps instead of typing. Buttons carry the
/// same commands a typed message would, so nothing here weakens the confirmation the agenda requires.</summary>
public sealed class AgentInteractionTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();

    static JsonElement Msg(object message) => JsonSerializer.SerializeToElement(message);
    static IEnumerable<(string Id, string Title)> Buttons(JsonElement interactive) => interactive.GetProperty("action").GetProperty("buttons").EnumerateArray().Select(b => (b.GetProperty("reply").GetProperty("id").GetString()!, b.GetProperty("reply").GetProperty("title").GetString()!));
    object Agenda(DateTimeOffset start, Guid doctor, int takenBy = 0) => new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = doctor, clinicianName = "Dra. Sintética Rivas", placeName = "Consultorio 1", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = start, durationMinutes = 30, takenBy, offered = true } } } } } } };

    [Theory]
    [InlineData("button_reply", "CONFIRMAR 1A2B3C", "Confirmar", "CONFIRMAR 1A2B3C")] // our command ids act exactly as if typed
    [InlineData("button_reply", "EMERGENCIA", "Sí, es emergencia", "EMERGENCIA")]
    [InlineData("button_reply", "ACTIVAR RECORDATORIOS", "Recordarme la cita", "ACTIVAR RECORDATORIOS")]
    [InlineData("list_reply", "CITA 2026-10-05T09:00:00-06:00 d2c5079c-e5da-4bb5-9029-b1e547ad8683 30 Dra. Sintética", "lun 5 oct 09:00", "CITA 2026-10-05T09:00:00-06:00 d2c5079c-e5da-4bb5-9029-b1e547ad8683 30 Dra. Sintética")]
    [InlineData("button_reply", "AGENDAR", "Agendar cita", "AGENDAR")]
    [InlineData("button_reply", "RECETA", "Mi receta", "RECETA")]
    [InlineData("button_reply", "otro", "Otro horario", "Otro horario")]                 // any other choice reads as its label
    [InlineData("button_reply", "DROP TABLE", "Agendar cita", "Agendar cita")]          // an id that is not one of ours is never trusted as text
    public void TappedChoiceReadsAsTheCommandOrItsLabel(string kind, string id, string title, string expected) =>
        Assert.Equal(expected, WhatsAppContent.Inbound(Msg(new { type = "interactive", interactive = new Dictionary<string, object> { ["type"] = kind, [kind] = new { id, title } } }), default));

    [Fact]
    public void TextAndMediaKeepTheirPreviousReading()
    {
        Assert.Equal("Hola", WhatsAppContent.Inbound(Msg(new { type = "text", text = new { body = "Hola" } }), default));
        Assert.Equal("Transcrito por Kapso", WhatsAppContent.Inbound(Msg(new { type = "audio" }), Msg(new { content = "Transcrito por Kapso" })));
        Assert.Equal("[Archivo recibido]", WhatsAppContent.Inbound(Msg(new { type = "image", image = new { id = "media" } }), default));
    }

    [Fact]
    public async Task ProposalIsConfirmedWithAButton()
    {
        await h.Link();
        await h.Runtime(h.Model(AgentHarness.ToolCall("propose_action", new { action = "cancel", appointmentId = Guid.NewGuid() }), AgentHarness.Reply("Puedo cancelar tu cita.")), h.Sender(), h.Hospital()).Run(h.Job, CancellationToken.None);

        var code = (await h.Db.Activities.SingleAsync(a => a.ConversationId == h.Conversation.Id && a.Kind.StartsWith("proposal:"))).Kind[9..];
        var buttons = Buttons(Assert.Single(h.Interactive)).ToList();
        Assert.Equal(("CONFIRMAR " + code, "Confirmar"), buttons[0]);
        Assert.Contains("CONFIRMAR " + code, Assert.Single(h.Sent)); // typing it still works: the text is always there
    }

    [Fact]
    public async Task FreeSlotsArriveAsAListAndTappingOneProposesItWithoutTheModel()
    {
        await h.Link();
        var doctor = Guid.NewGuid(); var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero); var hospital = h.Hospital(availability: Agenda(start, doctor));
        await h.Runtime(h.Model(AgentHarness.ToolCall("hospital_availability", new { date = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd") }), AgentHarness.Reply("Hay un horario libre.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        var list = Assert.Single(h.Interactive);
        Assert.Equal("list", list.GetProperty("type").GetString());
        var row = Assert.Single(list.GetProperty("action").GetProperty("sections")[0].GetProperty("rows").EnumerateArray());
        Assert.Contains("09:00", row.GetProperty("title").GetString());
        Assert.Contains("Dra. Sintética Rivas", row.GetProperty("description").GetString());
        var tapped = WhatsAppContent.Inbound(Msg(new { type = "interactive", interactive = new { type = "list_reply", list_reply = new { id = row.GetProperty("id").GetString(), title = row.GetProperty("title").GetString() } } }), default);

        // The tap is a complete instruction: no model is needed, or consulted, to turn it into a proposal.
        h.Interactive.Clear(); h.Sent.Clear(); await h.Say("patient", tapped);
        await h.Runtime(new AgentHarness.Fake(_ => throw new InvalidOperationException("The model must not be consulted")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        var proposal = await h.Db.Activities.SingleAsync(a => a.ConversationId == h.Conversation.Id && a.Kind.StartsWith("proposal:"));
        using var stored = JsonDocument.Parse(proposal.Body);
        Assert.Equal("create", stored.RootElement.GetProperty("action").GetString());
        Assert.Equal(doctor, stored.RootElement.GetProperty("doctorId").GetGuid());
        Assert.Equal(start, stored.RootElement.GetProperty("startsAt").GetDateTimeOffset());
        var sent = Assert.Single(h.Sent);
        Assert.Contains("a las 09:00", sent); Assert.Contains("Dra. Sintética Rivas", sent);
        Assert.Equal("CONFIRMAR " + proposal.Kind[9..], Buttons(Assert.Single(h.Interactive)).First().Id);
        Assert.Empty(h.HospitalWrites); // still nothing booked: the patient has to confirm
    }

    [Theory]
    [InlineData("CITA 2020-01-01T09:00:00-06:00 d2c5079c-e5da-4bb5-9029-b1e547ad8683 30 Dra. Sintética")] // a slot in the past
    [InlineData("CITA mañana d2c5079c-e5da-4bb5-9029-b1e547ad8683 30")]
    public async Task StaleOrMalformedSlotIsNeverProposed(string message)
    {
        await h.Link(); await h.Say("patient", message);

        await h.Runtime(new AgentHarness.Fake(_ => throw new InvalidOperationException("The model must not be consulted")), h.Sender(), h.Hospital()).Run(h.Job, CancellationToken.None);

        Assert.Empty(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id && a.Kind.StartsWith("proposal:")).ToListAsync());
        Assert.Contains("ya no está disponible", Assert.Single(h.Sent));
    }

    [Fact]
    public async Task ContactWithoutRecordGetsNoTappableSlots()
    {
        // Tapping a slot books for a registered patient; someone without a record is asked to register first, in words.
        var hospital = h.Hospital(availability: Agenda(new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero), Guid.NewGuid()));

        await h.Runtime(h.Model(AgentHarness.ToolCall("hospital_availability", new { date = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd") }), AgentHarness.Reply("Hay un horario libre.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        Assert.Empty(h.Interactive);
    }

    [Fact]
    public async Task EmergencyQuestionIsAnsweredWithAButton()
    {
        var hospital = h.Hospital(availability: new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = Array.Empty<object>() });

        await h.Runtime(h.Model(AgentHarness.ToolCall("hospital_availability", new { date = DateTime.UtcNow.AddHours(-6).ToString("yyyy-MM-dd") }), AgentHarness.Reply("Hoy no hay horarios.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        var buttons = Buttons(Assert.Single(h.Interactive)).ToList();
        Assert.Equal("EMERGENCIA", buttons[0].Id);
        Assert.False(AgentGuard.NamesEmergency(buttons[1].Title)); // «No» must not read as an emergency when tapped
    }

    [Theory]
    [InlineData("Hola", true)]
    [InlineData("buenas noches", true)]
    [InlineData("Hola, quiero una cita para mañana", false)] // a real request goes to the agent, not to a menu
    public async Task GreetingIsAnsweredAtOnceWithTheMenu(string message, bool menu)
    {
        await using var fresh = new AgentHarness(); await fresh.Start(message: message);
        var model = fresh.Model(AgentHarness.Reply("Respuesta del modelo"));

        await fresh.Runtime(model, fresh.Sender()).Run(fresh.Job, CancellationToken.None);

        Assert.Equal(menu ? 0 : 1, model.Calls);
        if (!menu) { Assert.Empty(fresh.Interactive); return; }
        var titles = Buttons(Assert.Single(fresh.Interactive)).Select(b => b.Title).ToList();
        Assert.Equal(3, titles.Count);
        Assert.NotNull(AgentGuard.Inbound(titles[2], "interactive")); // the third button reaches a person without the model
    }

    [Fact]
    public async Task BookedAppointmentIsStatedWithItsDateAndOffersReminders()
    {
        await h.Link();
        var doctor = Guid.NewGuid(); var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero);
        var hospital = new AgentHarness.Fake(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/v1/patients/")) return Task.FromResult(AgentHarness.Json(new { patientId = h.Contact.PatientId, phone = h.Contact.Phone }));
            if (path == "/v1/agenda/booking-options") return Task.FromResult(AgentHarness.Json(Agenda(start, doctor)));
            return Task.FromResult(AgentHarness.Json(new { appointmentId = Guid.NewGuid(), status = "booked", overlaps = false }));
        });
        await h.Say("patient", $"CITA {start.ToOffset(TimeSpan.FromHours(-6)):yyyy-MM-ddTHH:mm:sszzz} {doctor} 30 Dra. Sintética Rivas");
        await h.Runtime(h.Model(AgentHarness.Reply("No debe consultarse")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        var code = (await h.Db.Activities.SingleAsync(a => a.ConversationId == h.Conversation.Id && a.Kind.StartsWith("proposal:"))).Kind[9..];
        h.Sent.Clear(); h.Interactive.Clear(); await h.Say("patient", "CONFIRMAR " + code);

        await h.Runtime(h.Model(AgentHarness.Reply("No debe consultarse")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        var sent = Assert.Single(h.Sent);
        Assert.Contains("a las 09:00", sent); Assert.Contains("confirmó", sent);
        Assert.Contains(Buttons(Assert.Single(h.Interactive)), b => b.Id == "ACTIVAR RECORDATORIOS");
    }

    [Theory]
    [InlineData(1, true)]  // Hospital reports an overlap, but this appointment is the only active one in the slot: a cancelled one was counted
    [InlineData(2, false)] // someone else really holds the slot: a person sorts it out
    public async Task OverlapIsOnlyEscalatedWhenAnotherActiveAppointmentHoldsTheSlot(int activeInSlotAfterBooking, bool confirmed)
    {
        // Found booking against the real Hospital: its overlap probe ignores status, so a slot that once held a cancelled
        // appointment answers «overlaps» forever while the agenda shows it free. The patient was left without an answer.
        await h.Link();
        var doctor = Guid.NewGuid(); var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero); var booked = false;
        var hospital = new AgentHarness.Fake(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/v1/patients/")) return Task.FromResult(AgentHarness.Json(new { patientId = h.Contact.PatientId, phone = h.Contact.Phone }));
            if (path == "/v1/agenda/booking-options") return Task.FromResult(AgentHarness.Json(Agenda(start, doctor, booked ? activeInSlotAfterBooking : 0)));
            booked = true; return Task.FromResult(AgentHarness.Json(new { appointmentId = Guid.NewGuid(), status = "booked", overlaps = true }));
        });
        await h.Say("patient", $"CITA {start.ToOffset(TimeSpan.FromHours(-6)):yyyy-MM-ddTHH:mm:sszzz} {doctor} 30 Dra. Sintética Rivas");
        await h.Runtime(h.Model(AgentHarness.Reply("No debe consultarse")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        var code = (await h.Db.Activities.SingleAsync(a => a.ConversationId == h.Conversation.Id && a.Kind.StartsWith("proposal:"))).Kind[9..];
        h.Sent.Clear(); await h.Say("patient", "CONFIRMAR " + code);

        await h.Runtime(h.Model(AgentHarness.Reply("No debe consultarse")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        Assert.Equal(confirmed ? "agent" : "human", (await h.Fresh()).Status);
        Assert.Equal(confirmed, h.Sent.Any(text => text.Contains("confirmó tu cita")));
    }

    [Fact]
    public async Task MenuBooksAndDeliversThePrescriptionWithoutTheModel()
    {
        // Measured tonight: the AI provider rate-limits for long windows. The two things patients ask for most must not depend on it.
        await h.Link();
        var doctor = Guid.NewGuid(); var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1).AddHours(15), TimeSpan.Zero); var prescription = Guid.NewGuid();
        var signed = new { prescriptionId = prescription, patientId = h.Contact.PatientId, encounterId = Guid.NewGuid(), state = "signed", signedAt = DateTimeOffset.UtcNow, contentWithheld = false, lines = Array.Empty<object>() };
        var hospital = h.Hospital(signed, availability: Agenda(start, doctor));
        var noModel = new AgentHarness.Fake(_ => throw new InvalidOperationException("The model must not be consulted"));

        await h.Say("patient", "AGENDAR");
        await h.Runtime(noModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        var list = Assert.Single(h.Interactive);
        Assert.Equal("list", list.GetProperty("type").GetString());
        Assert.StartsWith("CITA ", list.GetProperty("action").GetProperty("sections")[0].GetProperty("rows")[0].GetProperty("id").GetString());

        await h.Say("patient", "RECETA");
        await h.Runtime(noModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        Assert.Equal(1, h.Documents);
        Assert.Equal("agent", (await h.Fresh()).Status);
    }

    [Fact]
    public async Task MenuWithNothingToOfferSaysSoAndReachesReception()
    {
        await h.Link();
        var hospital = h.Hospital(availability: new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = Array.Empty<object>() });
        var noModel = new AgentHarness.Fake(_ => throw new InvalidOperationException("The model must not be consulted"));

        await h.Say("patient", "RECETA");
        await h.Runtime(noModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        Assert.Contains("No tienes recetas emitidas", h.Sent[^1]); Assert.Equal(0, h.Documents);

        await h.Say("patient", "AGENDAR");
        await h.Runtime(noModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        Assert.Equal("human", (await h.Fresh()).Status); // no published hours: a person takes it from here
    }

    [Fact]
    public async Task MenuForSomeoneWithoutRecordStartsTheRegistrationForm()
    {
        // Booking needs a record first. The form asks for it one answer at a time and needs no model (AgentIntakeFlowTests).
        await h.Say("patient", "AGENDAR");
        var model = h.Model(AgentHarness.Reply("No debe consultarse"));

        await h.Runtime(model, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal(0, model.Calls);
        Assert.Contains("Paso 1 de 7", Assert.Single(h.Sent));
    }

    [Fact]
    public async Task WhenTheModelIsDownARegisteredPatientStillGetsTheMenu()
    {
        // Happened on the real number on 2026-10-04: the AI provider was rate-limited, and a registered patient who only
        // wanted an appointment was handed to a person at midnight, although booking from the menu needs no model.
        await h.Link();
        var down = new AgentHarness.Fake(_ => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") }));

        await h.Runtime(down, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("agent", (await h.Fresh()).Status);
        var ids = Buttons(Assert.Single(h.Interactive)).Select(b => b.Id).ToList();
        Assert.Contains("AGENDAR", ids); Assert.Contains("RECETA", ids);
        Assert.Contains(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).ToListAsync(), a => a.Kind == "agent_provider");
    }

    [Fact]
    public async Task GreetingGetsTheMenuEvenInTheMiddleOfAConversation()
    {
        await h.Say("agent", "¿Te ayudo con algo más?"); await h.Say("patient", "Hola");
        var model = h.Model(AgentHarness.Reply("Respuesta del modelo"));

        await h.Runtime(model, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal(0, model.Calls);
        Assert.Single(h.Interactive);
    }

    [Fact]
    public async Task ReplyTooLongForButtonsIsStillSentAsText()
    {
        await h.Link();
        var reply = string.Join(" ", Enumerable.Repeat("Puedo cancelar tu cita si lo confirmas.", 40)); // WhatsApp caps an interactive body at 1024 characters

        await h.Runtime(h.Model(AgentHarness.ToolCall("propose_action", new { action = "cancel", appointmentId = Guid.NewGuid() }), AgentHarness.Reply(reply)), h.Sender(), h.Hospital()).Run(h.Job, CancellationToken.None);

        Assert.Empty(h.Interactive);
        Assert.Contains("CONFIRMAR", Assert.Single(h.Sent));
    }

    [Theory]
    [InlineData("true", 1)]
    [InlineData(null, 0)]
    public async Task PatientSeesTypingWhileTheModelWorks(string? enabled, int expected)
    {
        await h.Runtime(h.Model(AgentHarness.Reply("Respuesta sintética")), h.Sender(), extra: new() { ["KAPSO_TYPING_INDICATOR"] = enabled }).Run(h.Job, CancellationToken.None);

        Assert.Equal(expected, h.Typing);
        Assert.Equal(["Respuesta sintética"], h.Sent);
    }
}
