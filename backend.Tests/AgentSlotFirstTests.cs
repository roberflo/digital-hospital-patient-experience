using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>docs/agent-slot-first.md, T-3…T-13: someone without a record picks the hour first. The hour is working state of
/// the form, never a reservation; one card and one Confirmar register the person and book the appointment. Synthetic people only.</summary>
public sealed class AgentSlotFirstTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    static readonly AgentHarness.Fake NoModel = new(_ => throw new InvalidOperationException("The model must not be consulted"));
    static readonly string[] Answers = ["Ana Sintética", "López Prueba", "12/03/1990", "Femenino", "Carlos Sintético", "Hermano", "7000 0001"];
    static readonly Guid Doctor = Guid.NewGuid(), Patient = Guid.NewGuid();
    static readonly DateTimeOffset Start = new(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero); // 09:00 at the hospital
    static string Tap(DateTimeOffset start) => $"CITA {start.ToOffset(TimeSpan.FromHours(-6)):yyyy-MM-ddTHH:mm:sszzz} {Doctor} 30 Dra. Sintética Rivas";
    static string Stamp(DateTimeOffset start) => start.ToOffset(TimeSpan.FromHours(-6)).ToString("yyyy-MM-ddTHH:mm");

    /// <summary>Every request Hospital saw, in order, as «METHOD path».</summary>
    readonly List<string> seen = []; readonly List<JsonElement> bodies = [];
    int takenBy; bool duplicate, soon; HttpStatusCode agenda = HttpStatusCode.OK, verify = HttpStatusCode.OK;
    AgentHarness.Fake Hospital() => new(async request =>
    {
        var path = request.RequestUri!.AbsolutePath; seen.Add($"{request.Method} {path}");
        if (request.Method == HttpMethod.Post) bodies.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone());
        if (path == "/v1/patients" && duplicate) return AgentHarness.Json(new { created = false, duplicateCandidates = new[] { new { patientId = Guid.NewGuid(), displayName = "Ana S. L.", birthDate = "1990-03-12", matchedOn = "name-and-birth-date" } } });
        if (path == "/v1/patients") { var created = AgentHarness.Json(new { created = true, patientId = Patient, duplicateCandidates = Array.Empty<object>() }); created.StatusCode = HttpStatusCode.Created; return created; }
        if (path == $"/v1/patients/{Patient}") { var found = AgentHarness.Json(new { patientId = Patient, givenNames = "Ana Sintética", familyNames = "López Prueba", phone = h.Contact.Phone }); found.StatusCode = verify; return found; }
        if (path == "/v1/agenda/booking-options") return AgentHarness.Json(new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = Doctor, clinicianName = "Dra. Sintética Rivas", placeName = "Consultorio 1", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = Start, durationMinutes = 30, takenBy, offered = true }, new { slotId = "b", startsAt = Start.AddHours(1), durationMinutes = 30, takenBy = 0, offered = true }, new { slotId = "c", startsAt = DateTimeOffset.UtcNow.AddHours(2), durationMinutes = 30, takenBy = soon ? 0 : 1, offered = true } } } } } } });
        if (path == "/v1/agenda" && request.Method == HttpMethod.Post) { var booked = AgentHarness.Json(new { appointmentId = Guid.NewGuid(), status = "booked", overlaps = false }); booked.StatusCode = agenda; return booked; }
        throw new InvalidOperationException("Unexpected hospital request " + path);
    });
    async Task<string> Say(string text, HttpMessageHandler? model = null) { await h.Say("patient", text); await h.Runtime(model ?? NoModel, h.Sender(), Hospital()).Run(h.Job, CancellationToken.None); return h.Sent[^1]; }
    Task<List<Activity>> Trail() => h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).OrderBy(a => a.CreatedAt).ToListAsync();
    async Task<Activity> Form() => Assert.Single(await Trail(), a => a.Kind == "intake");
    /// <summary>T-12, INV-T-2: before Confirmar nothing tells the patient the hour is already theirs, in any tense («apartada», «te aparté», «reservamos»).
    /// «Para apartar el…» is what the hour is for, not a claim.</summary>
    internal static void ClaimsNoHour(string message) => Assert.DoesNotMatch(@"(?i)\b(apart|reserv|agend)(ad[ao]s?|é|amos|aste)\b", message);
    static void ReadsLikeAChat(string message) => Assert.All(message.Split('\n'), line => Assert.True(line.Length <= 130, $"A line of {line.Length} characters reads as a paragraph: «{line}»"));
    /// <summary>The tap that opens the form. Every journey here goes through it: without the form the rest would be measuring nothing.</summary>
    async Task<string> TapAnHour() { var asked = await Say(Tap(Start)); await Form(); return asked; }

    [Fact]
    public async Task TappingAFreeHourWithoutARecordKeepsItAndOpensTheForm()
    {
        var asked = await Say(Tap(Start));

        Assert.Contains(Stamp(Start), (await Form()).Body); // the hour is kept with the form, and only there
        Assert.StartsWith("Para apartar el *", asked); Assert.Contains("a las 09:00", asked); Assert.Contains("son 7 datos cortos", asked); Assert.Contains("¿Cuál es tu nombre?", asked);
        Assert.DoesNotContain(await Trail(), a => a.Kind.StartsWith("proposal:"));
        Assert.Empty(seen); // INV-T-1: choosing an hour asks nothing of Hospital
    }

    [Theory]
    [InlineData("CITA 2020-01-01T09:00:00-06:00 d2c5079c-e5da-4bb5-9029-b1e547ad8683 30 Dra. Sintética")] // a slot in the past
    [InlineData("CITA mañana d2c5079c-e5da-4bb5-9029-b1e547ad8683 30")]
    [InlineData("CITA 2099-01-01T09:00:00-06:00 d2c5079c-e5da-4bb5-9029-b1e547ad8683 3 Dra. Sintética")] // a duration Hospital would refuse
    [InlineData("CITA 2099-01-01T09:00:00-06:00 00000000-0000-0000-0000-000000000000 30 Dra. Sintética")] // no doctor
    public async Task AStaleOrMalformedHourOpensNothingWithoutARecord(string message)
    {
        var reply = await Say(message);

        Assert.Contains("ya no está disponible", Assert.Single(h.Sent)); Assert.Equal(reply, h.Sent[0]);
        Assert.DoesNotContain(await Trail(), a => a.Kind == "intake" || a.Kind.StartsWith("proposal:"));
        Assert.Empty(seen);
    }

    [Theory]
    [InlineData("salir")]
    [InlineData("¿A qué hora abren los sábados?")]
    public async Task LeavingTheFormForgetsTheChosenHour(string message)
    {
        await TapAnHour(); await Say("Ana Sintética");

        await Say(message, h.Model(AgentHarness.Reply("No tengo ese dato; ¿te paso con recepción?")));

        var trail = await Trail();
        Assert.DoesNotContain(trail, a => a.Kind == "intake");
        Assert.All(trail, a => Assert.DoesNotContain(Stamp(Start), a.Body)); // INV-T-5: no activity keeps the hour
        Assert.Empty(seen); // and nothing was ever asked of Hospital for it
    }

    [Fact]
    public async Task AfterThirtyMinutesTheChosenHourIsGone()
    {
        await TapAnHour();
        var form = await Form(); form.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-31); await h.Db.SaveChangesAsync();
        var model = h.Model(AgentHarness.Reply("No tengo ese dato; ¿te paso con recepción?"));

        await Say("Ana Sintética", model);

        Assert.DoesNotContain(await Trail(), a => a.Kind == "intake");
        Assert.Equal(1, model.Calls); // the form is over: a name alone is an ordinary message again
    }

    [Fact]
    public async Task TheFinishedFormIsOneCardWithTheDataAndTheAppointment()
    {
        await TapAnHour(); var card = ""; foreach (var answer in Answers) card = await Say(answer);

        Assert.Contains("*Ana Sintética López Prueba*\nNacimiento: 12 de marzo de 1990\nSexo: femenino", card);
        Assert.Contains("\n\n*Cita:* ", card); Assert.Contains("a las 09:00\n*Con:* Dra. Sintética Rivas\n\n", card);
        Assert.EndsWith("¿Confirmo tu registro y tu cita?", card);
        Assert.Equal(["Confirmar", "Corregir datos", "Otro horario"], AgentHarness.Options(h.Interactive[^1]).Select(option => option.Title));
        Assert.All(h.Sent, ReadsLikeAChat);
        var trail = await Trail();
        using var stored = JsonDocument.Parse(Assert.Single(trail, a => a.Kind.StartsWith("proposal:")).Body); // one card for one decision
        Assert.Equal(("register", Start, Doctor, 30), (stored.RootElement.GetProperty("action").GetString(), stored.RootElement.GetProperty("startsAt").GetDateTimeOffset(), stored.RootElement.GetProperty("doctorId").GetGuid(), stored.RootElement.GetProperty("durationMinutes").GetInt32()));
        Assert.DoesNotContain(trail, a => a.Kind == "intake"); // the hour left the form with the data
        Assert.Empty(seen); // nothing reaches Hospital before the patient confirms
        Assert.All(h.Sent, ClaimsNoHour); // T-12, INV-T-2: nothing says the hour is already taken for them
    }

    [Fact]
    public async Task AnHourThatPassedDuringTheFormIsLeftOutOfTheCard()
    {
        await TapAnHour(); foreach (var answer in Answers[..6]) await Say(answer);
        var form = await Form(); Assert.Contains(Stamp(Start), form.Body); form.Body = form.Body.Replace(Stamp(Start)[..10], "2020-01-01"); await h.Db.SaveChangesAsync();

        var card = await Say(Answers[6]);

        Assert.DoesNotContain("*Cita:*", card); Assert.EndsWith("¿Están correctos?", card);
        Assert.DoesNotContain("startsAt", Assert.Single(await Trail(), a => a.Kind.StartsWith("proposal:")).Body);
    }

    /// <summary>Hour tapped, seven answers, one card: its code. Nothing has reached Hospital yet.</summary>
    async Task<Activity> Card() { await TapAnHour(); foreach (var answer in Answers) await Say(answer); Assert.Empty(seen); return Assert.Single(await Trail(), a => a.Kind.StartsWith("proposal:")); }
    int Posts(string path) => seen.Count(x => x == "POST " + path);
    static string Day => $"{Start.ToOffset(TimeSpan.FromHours(-6)).Day} de ";

    [Fact]
    public async Task ConfirmingChecksTheHourRegistersLinksAndBooksInThatOrder()
    {
        var code = (await Card()).Kind[9..];

        var done = await Say("CONFIRMAR " + code);

        // The hour is checked before anything is written; the record is created, verified and linked; only then the appointment. No POST twice.
        Assert.Equal(["GET /v1/agenda/booking-options", "POST /v1/patients", $"GET /v1/patients/{Patient}", $"GET /v1/patients/{Patient}", "POST /v1/agenda"], seen);
        Assert.Equal(h.Contact.Phone, bodies[0].GetProperty("phone").GetString()); Assert.False(bodies[0].GetProperty("forceCreateDespiteDuplicate").GetBoolean());
        Assert.Equal(("first-visit", Patient, Doctor, Start), (bodies[1].GetProperty("visitKind").GetString(), bodies[1].GetProperty("patientId").GetGuid(), bodies[1].GetProperty("clinicianId").GetGuid(), bodies[1].GetProperty("startsAt").GetDateTimeOffset()));
        await h.Db.Entry(h.Contact).ReloadAsync(); Assert.Equal(Patient, h.Contact.PatientId);
        Assert.StartsWith("Listo, Ana, ya te registré", done); Assert.Contains("quedó agendada", done); Assert.Contains("a las 09:00", done); ReadsLikeAChat(done);
        Assert.Contains(AgentHarness.Options(h.Interactive[^1]), option => option.Id == "ACTIVAR RECORDATORIOS");
        Assert.Equal("agent", (await h.Fresh()).Status);
        var trail = await Trail();
        Assert.Contains(trail, a => a.Kind == "patient_registered"); Assert.Contains(trail, a => a.Kind == "appointment");
        Assert.DoesNotContain("Sintética", Assert.Single(trail, a => a.Kind.StartsWith("proposal_used:")).Body); // the confirmed card keeps no personal data behind
    }

    async Task RegisteredWithoutAnAppointmentAndOfferedOtherHours(string reply)
    {
        Assert.Equal((1, 0), (Posts("/v1/patients"), Posts("/v1/agenda")));
        await h.Db.Entry(h.Contact).ReloadAsync(); Assert.Equal(Patient, h.Contact.PatientId);
        Assert.StartsWith("Ya te registré. Ese horario se acaba de ocupar.\n", reply); Assert.DoesNotContain("quedó agendada", reply); ReadsLikeAChat(reply);
        Assert.Equal("list", h.Interactive[^1].GetProperty("type").GetString());
        Assert.All(AgentHarness.Options(h.Interactive[^1]), option => Assert.StartsWith("CITA ", option.Id));
        Assert.Equal("agent", (await h.Fresh()).Status);
    }

    [Fact]
    public async Task AnHourTakenAtConfirmingStillRegistersAndOffersOthers()
    {
        var code = (await Card()).Kind[9..]; takenBy = 1; // someone else took the hour while the form was being filled

        var reply = await Say("CONFIRMAR " + code);

        await RegisteredWithoutAnAppointmentAndOfferedOtherHours(reply);
        Assert.Equal("GET /v1/agenda/booking-options", seen[0]); // it was checked before the record was created
        Assert.DoesNotContain(AgentHarness.Options(h.Interactive[^1]), option => option.Id.Contains(Stamp(Start))); // the list is read again: the taken hour is not in it
    }

    [Fact]
    public async Task AnHourAlreadyPastAtConfirmingIsTreatedAsTaken()
    {
        var card = await Card(); var day = Start.ToString("yyyy-MM-dd");
        Assert.Contains(day, card.Body); card.Body = card.Body.Replace(day, "2020-01-01"); await h.Db.SaveChangesAsync();

        var reply = await Say("CONFIRMAR " + card.Kind[9..]);

        await RegisteredWithoutAnAppointmentAndOfferedOtherHours(reply);
    }

    [Fact]
    public async Task APossibleDuplicateBooksNothingAndTellsTheTeamTheHourWanted()
    {
        var code = (await Card()).Kind[9..]; duplicate = true;

        await Say("CONFIRMAR " + code);

        Assert.False(bodies.Single().GetProperty("forceCreateDespiteDuplicate").GetBoolean());
        Assert.Equal((1, 0), (Posts("/v1/patients"), Posts("/v1/agenda")));
        await h.Db.Entry(h.Contact).ReloadAsync(); Assert.Null(h.Contact.PatientId);
        Assert.Equal("agent", (await h.Fresh()).Status);
        var reason = Assert.Single(await Trail(), a => a.Kind == "handoff_offer").Body; // the patient is asked, with a button, whether to go to a person
        Assert.Contains(Day, reason); Assert.Contains("a las 09:00", reason); // and the team reads which hour was wanted
        Assert.DoesNotContain(h.Sent, text => text.Contains("quedó agendada"));
    }

    [Fact]
    public async Task WhenBookingFailsAfterRegisteringAPersonTakesOverAndNothingIsRetried()
    {
        var code = (await Card()).Kind[9..]; agenda = HttpStatusCode.InternalServerError;

        await Say("CONFIRMAR " + code);

        Assert.Equal("human", (await h.Fresh()).Status);
        Assert.Equal((1, 1), (Posts("/v1/patients"), Posts("/v1/agenda"))); // INV-T-4: neither POST is repeated
        await h.Db.Entry(h.Contact).ReloadAsync(); Assert.Equal(Patient, h.Contact.PatientId); // the record exists and stays linked
        var trail = await Trail();
        Assert.Contains(trail, a => a.Kind == "patient_registered"); Assert.DoesNotContain(trail, a => a.Kind == "appointment");
        Assert.DoesNotContain(h.Sent, text => text.Contains("quedó agendada"));
        var reason = Assert.Single(trail, a => a.Kind == "handoff").Body;
        Assert.Contains("registrado", reason); Assert.Contains(Day, reason); Assert.Contains("a las 09:00", reason); // the team reads what was done and which hour was wanted
    }

    [Fact]
    public async Task WhenTheNewRecordCannotBeVerifiedNothingIsBookedAndTheTeamReadsWhichRecord()
    {
        var code = (await Card()).Kind[9..]; verify = HttpStatusCode.InternalServerError;

        await Say("CONFIRMAR " + code);

        Assert.Equal((1, 0), (Posts("/v1/patients"), Posts("/v1/agenda"))); // INV-T-3: no appointment without a verified, linked record
        Assert.Equal("human", (await h.Fresh()).Status);
        await h.Db.Entry(h.Contact).ReloadAsync(); Assert.Null(h.Contact.PatientId);
        Assert.DoesNotContain(h.Sent, text => text.Contains("quedó agendada"));
        Assert.All(h.Sent, text => Assert.DoesNotContain(Patient.ToString(), text)); // the reference is for the team, never for the chat
        Assert.Contains(Patient.ToString(), Assert.Single(await Trail(), a => a.Kind == "handoff").Body); // the record Hospital did create: reception reconciles that one instead of registering again
    }

    [Fact]
    public async Task ACardHospitalWouldRefuseGoesToAPersonAtConfirming()
    {
        var card = await Card();
        Assert.Contains("\"durationMinutes\":30", card.Body); card.Body = card.Body.Replace("\"durationMinutes\":30", "\"durationMinutes\":3"); await h.Db.SaveChangesAsync();

        await Say("CONFIRMAR " + card.Kind[9..]);

        Assert.Equal("human", (await h.Fresh()).Status); // told a person takes over, not left in silence with a failed job
        Assert.DoesNotContain(seen, request => request.StartsWith("POST "));
        Assert.Contains(await Trail(), a => a.Kind == "handoff");
    }

    [Theory]
    [InlineData("Sí")]
    [InlineData("ok, el viernes")]
    public async Task YesAfterAskingForAnotherHourConfirmsNothing(string answer)
    {
        var code = (await Card()).Kind[9..]; soon = true; // a free hour today: the list asks nothing about an emergency, so «sí» can only be about the appointment
        await Say("AGENDAR"); // «Otro horario»: the hour on the card was turned down
        Assert.StartsWith("Hay espacio el *", h.Sent[^1]); Assert.DoesNotContain(AgentGuard.EmergencyQuestion, h.Sent[^1]); ClaimsNoHour(h.Sent[^1]); var before = h.Sent.Count;

        await Say(answer, h.Model(AgentHarness.Reply("¿Qué día prefieres?")));

        Assert.DoesNotContain(seen, request => request.StartsWith("POST ")); // «sí» answers the list, not the card above it
        Assert.Equal("proposal:" + code, Assert.Single(await Trail(), a => a.Kind.StartsWith("proposal")).Kind); // the card was not consumed
        Assert.True(h.Sent.Count > before, "The patient was left without an answer");
    }

    [Fact]
    public async Task YesAfterAskingForAnotherHourConfirmsNothingWithARecord()
    {
        h.Contact.PatientId = Patient; await h.Db.SaveChangesAsync();
        await Say(Tap(Start)); Assert.EndsWith("¿La confirmo?", h.Sent[^1]);
        var code = Assert.Single(await Trail(), a => a.Kind.StartsWith("proposal:")).Kind;
        soon = true; await Say("AGENDAR"); // an agent message between the card and the «sí»
        Assert.StartsWith("Hay espacio el *", h.Sent[^1]); Assert.DoesNotContain(AgentGuard.EmergencyQuestion, h.Sent[^1]); var before = h.Sent.Count;

        await Say("Sí", h.Model(AgentHarness.Reply("¿Qué día prefieres?")));

        Assert.DoesNotContain(seen, request => request.StartsWith("POST "));
        Assert.Equal(code, Assert.Single(await Trail(), a => a.Kind.StartsWith("proposal")).Kind);
        Assert.True(h.Sent.Count > before, "The patient was left without an answer");
    }

    [Fact]
    public async Task AMenuCommandInTheMiddleOfTheFormClosesItAndIsServed()
    {
        await TapAnHour();

        var reply = await Say("AGENDAR"); // the old card's «Otro horario» is still on screen after «Corregir datos»

        Assert.StartsWith("Hay espacio el *", reply); Assert.Equal("list", h.Interactive[^1].GetProperty("type").GetString());
        var trail = await Trail();
        Assert.DoesNotContain(trail, a => a.Kind == "intake");
        Assert.All(trail, a => Assert.DoesNotContain("AGENDAR", a.Body)); // the command was never kept as a name
        Assert.All(h.Sent, text => Assert.DoesNotContain("AGENDAR", text));
    }

    const string Expired = "ya venció o no existe";

    [Fact]
    public async Task CorrectingTheDataRestartsTheFormAndKeepsTheHour()
    {
        var code = (await Card()).Kind[9..];
        Assert.Equal(["CONFIRMAR " + code, "CORREGIR " + code, "AGENDAR"], AgentHarness.Options(h.Interactive[^1]).Select(option => option.Id));

        var asked = await Say("CORREGIR " + code);

        using (var form = JsonDocument.Parse((await Form()).Body)) Assert.Equal((1, Tap(Start), JsonValueKind.Null), (form.RootElement.GetProperty("step").GetInt32(), form.RootElement.GetProperty("slot").GetString(), form.RootElement.GetProperty("givenNames").ValueKind));
        Assert.StartsWith("Para apartar el *", asked); Assert.Contains("a las 09:00", asked); Assert.Contains("¿Cuál es tu nombre?", asked); ReadsLikeAChat(asked); ClaimsNoHour(asked);
        Assert.All(await Trail(), a => { Assert.False(a.Kind.StartsWith("proposal")); Assert.DoesNotContain("Sintética López", a.Body); }); // the card is gone with its data, and it was never confirmed
        Assert.Empty(seen);
        Assert.Contains(Expired, await Say("CONFIRMAR " + code));
        Assert.Empty(seen);
    }

    [Fact]
    public async Task AnotherHourReissuesTheCardWithTheSameData()
    {
        var code = (await Card()).Kind[9..];

        ClaimsNoHour(await Say("AGENDAR")); // «Otro horario»
        Assert.Equal("list", h.Interactive[^1].GetProperty("type").GetString());
        var card = await Say(AgentHarness.Options(h.Interactive[^1]).Single(option => option.Id == Tap(Start.AddHours(1))).Id);

        Assert.Contains("*Ana Sintética López Prueba*\nNacimiento: 12 de marzo de 1990\nSexo: femenino\nContacto de emergencia: Carlos Sintético (Hermano), 70000001", card);
        Assert.Contains("a las 10:00\n*Con:* Dra. Sintética Rivas", card); Assert.EndsWith("¿Confirmo tu registro y tu cita?", card); ReadsLikeAChat(card); ClaimsNoHour(card);
        var trail = await Trail();
        var reissued = Assert.Single(trail, a => a.Kind.StartsWith("proposal")); // one card, and the old one was never confirmed
        Assert.NotEqual(code, reissued.Kind[9..]);
        using (var stored = JsonDocument.Parse(reissued.Body)) Assert.Equal(Start.AddHours(1), stored.RootElement.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal("CONFIRMAR " + reissued.Kind[9..], AgentHarness.Options(h.Interactive[^1])[0].Id);
        Assert.DoesNotContain(trail, a => a.Kind == "intake");
        Assert.Contains(Expired, await Say("CONFIRMAR " + code));
        Assert.All(seen, request => Assert.Equal("GET /v1/agenda/booking-options", request)); // the agenda was read; nothing was written
    }

    [Fact]
    public async Task ProposingForSomeoneWithoutARecordPointsToTheList()
    {
        // T-15, the half the suite can see: a model that proposes an appointment to someone without a record is told what to do instead of to hand off.
        var asked = new List<string>(); var turn = 0;
        var model = new AgentHarness.Fake(async request =>
        {
            asked.Add(await request.Content!.ReadAsStringAsync());
            return turn++ == 0 ? AgentHarness.ToolCall("propose_action", new { action = "create", doctorId = Doctor, startsAt = Start, durationMinutes = 30 }) : AgentHarness.Reply("Toca un horario de la lista, por favor.");
        });

        await h.Runtime(model, h.Sender(), Hospital()).Run(h.Job, CancellationToken.None);

        Assert.Contains("toque un horario de la lista", asked[1]); Assert.Contains("No derives", asked[1]);
        Assert.DoesNotContain(await Trail(), a => a.Kind.StartsWith("proposal") || a.Kind.StartsWith("handoff"));
        Assert.Empty(seen); Assert.Equal("agent", (await h.Fresh()).Status);
    }
}
