using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>docs/reception-agent.md, criterios 34–37: a new client registers one answer at a time, with no model,
/// and sees the free hours as soon as the record exists. Synthetic people only.</summary>
public sealed class AgentIntakeFlowTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    static readonly DateOnly Today = new(2026, 10, 4);
    static readonly string[] Answers = ["Ana Sintética", "López Prueba", "12/03/1990", "Femenino", "Carlos Sintético", "Hermano", "7000 0001"];

    [Fact]
    public void SevenAnswersCompleteARegistration()
    {
        var (state, prompt, _) = Intake.Start();
        Assert.Contains("nombres", prompt);
        foreach (var answer in Answers) (state, prompt, _) = state.Answer(answer, Today);

        Assert.True(state.Complete);
        Assert.Equal(("Ana Sintética", "López Prueba", "1990-03-12", "female", "Carlos Sintético", "Hermano", "70000001"),
            (state.GivenNames, state.FamilyNames, state.BirthDate, state.Sex, state.EmergencyName, state.EmergencyRelationship, state.EmergencyPhone));
    }

    [Theory]
    [InlineData("12/03/1990", "1990-03-12")]
    [InlineData("12-3-1990", "1990-03-12")]
    [InlineData("12 de marzo de 1990", "1990-03-12")]
    [InlineData("1 de setiembre del 1985", "1985-09-01")]
    [InlineData("1990-03-12", "1990-03-12")]
    [InlineData("30/02/1990", null)]          // not a date
    [InlineData("marzo del 90", null)]
    [InlineData("12/03/2030", null)]          // not born yet
    public void BirthDateIsReadTheWayPeopleWriteIt(string written, string? expected)
    {
        var state = new Intake(3, "Ana", "López");
        var (next, prompt, _) = state.Answer(written, Today);
        Assert.Equal(expected, next.BirthDate);
        Assert.Equal(expected is null ? 3 : 4, next.Step);
        if (expected is null) Assert.Contains("fecha", prompt);
    }

    [Theory]
    [InlineData(1, "12345")]                  // a name has letters
    [InlineData(1, "A")]
    [InlineData(4, "no sé")]                  // only the two registral values
    [InlineData(7, "123")]                    // not a phone
    public void AnAnswerThatDoesNotFitIsAskedAgainNeverGuessed(int step, string answer)
    {
        var state = new Intake(step, "Ana", "López", "1990-03-12", "female", "Carlos", "Hermano");
        var (next, _, _) = state.Answer(answer, Today);
        Assert.Equal(step, next.Step);
    }

    [Fact]
    public void AMinorIsNotRegisteredByChat()
    {
        var (next, _, _) = new Intake(3, "Luis", "Prueba").Answer("03/05/2015", Today);
        Assert.True(next.Minor);
    }

    [Fact]
    public void SexIsAnsweredWithButtons()
    {
        var (_, _, choices) = new Intake(3, "Ana", "López").Answer("12/03/1990", Today);
        Assert.Equal(["Femenino", "Masculino"], Assert.IsType<Choices>(choices).Options.Select(o => o.Title));
    }

    [Fact]
    public async Task NewClientTapsAgendarRegistersStepByStepAndSeesTheFreeHoursWithoutTheModel()
    {
        var patient = Guid.NewGuid(); var doctor = Guid.NewGuid(); var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1).AddHours(15), TimeSpan.Zero); var posts = new List<JsonElement>();
        var hospital = new AgentHarness.Fake(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/patients") { posts.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone()); var created = AgentHarness.Json(new { created = true, patientId = patient, duplicateCandidates = Array.Empty<object>() }); created.StatusCode = HttpStatusCode.Created; return created; }
            if (path.StartsWith("/v1/patients/")) return AgentHarness.Json(new { patientId = patient, givenNames = "Ana Sintética", familyNames = "López Prueba", phone = h.Contact.Phone });
            if (path == "/v1/agenda/booking-options") return AgentHarness.Json(new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = doctor, clinicianName = "Dra. Sintética Rivas", placeName = "Consultorio 1", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = start, durationMinutes = 30, takenBy = 0, offered = true } } } } } } });
            throw new InvalidOperationException("Unexpected hospital request " + path);
        });
        var noModel = new AgentHarness.Fake(_ => throw new InvalidOperationException("The model must not be consulted"));
        async Task<string> Say(string text) { await h.Say("patient", text); await h.Runtime(noModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None); return h.Sent[^1]; }

        Assert.Contains("Paso 1 de 7", await Say("AGENDAR"));
        string reply = ""; foreach (var answer in Answers) reply = await Say(answer);

        Assert.Empty(posts); // nothing reaches Hospital before the patient confirms
        Assert.Contains("*Ana Sintética López Prueba*\nNacimiento: 12 de marzo de 1990\nSexo: femenino", reply);
        var confirm = h.Interactive[^1].GetProperty("action").GetProperty("buttons")[0].GetProperty("reply").GetProperty("id").GetString()!;
        var registered = await Say(confirm);

        Assert.Equal(h.Contact.Phone, Assert.Single(posts).GetProperty("phone").GetString());
        Assert.Contains("ya tienes tu expediente", registered);
        Assert.Equal("list", h.Interactive[^1].GetProperty("type").GetString()); // the free hours come with the confirmation, unasked
        Assert.Equal("agent", (await h.Fresh()).Status);
        Assert.DoesNotContain(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id && a.Kind.StartsWith("intake")).ToListAsync(), a => a.Body.Contains("Sintética")); // the finished form keeps no personal data
    }

    [Fact]
    public async Task AQuestionInTheMiddleOfTheFormIsNotTakenAsAnAnswer()
    {
        var model = h.Model(AgentHarness.Reply("No tengo ese dato; ¿te paso con recepción?")); // no hours here: this hospital's guide is empty, and an hour nobody published would be withheld
        await h.Say("patient", "AGENDAR"); await h.Runtime(model, h.Sender()).Run(h.Job, CancellationToken.None);
        await h.Say("patient", "¿A qué hora abren los sábados?");

        await h.Runtime(model, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal(1, model.Calls); // the agent answers it
        await h.Say("patient", "Ana Sintética");
        await h.Runtime(model, h.Sender()).Run(h.Job, CancellationToken.None);
        Assert.Equal(2, model.Calls); // and the form is over: a name alone is a normal message again
    }

    [Fact]
    public async Task AMinorInTheFormIsOfferedReception()
    {
        var noModel = new AgentHarness.Fake(_ => throw new InvalidOperationException("The model must not be consulted"));
        foreach (var text in new[] { "AGENDAR", "Luis Sintético", "Prueba", "03/05/2015" }) { await h.Say("patient", text); await h.Runtime(noModel, h.Sender()).Run(h.Job, CancellationToken.None); }

        Assert.Equal("agent", (await h.Fresh()).Status);
        Assert.Contains(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).ToListAsync(), a => a.Kind == "handoff_offer"); // the patient is asked, with a button, whether to go to a person
    }

    [Fact]
    public async Task WhenTheModelIsDownANewClientGetsTheMenuToo()
    {
        var down = new AgentHarness.Fake(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") }));

        await h.Runtime(down, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("agent", (await h.Fresh()).Status);
        Assert.Contains(AgentHarness.Options(h.Interactive[^1]), option => option.Id == "AGENDAR");
    }

    [Fact]
    public void WhatThePatientAlreadySaidIsNotAskedAgain()
    {
        // Read in the evals: the patient had given name and birth date, and the form started over at «¿cuáles son tus nombres?».
        var (state, prompt, choices) = Intake.Start(Today, "Rosa Sintética", "Prueba", "8 de enero de 1985");

        Assert.Equal(4, state.Step);
        Assert.Equal(("Rosa Sintética", "Prueba", "1985-01-08"), (state.GivenNames, state.FamilyNames, state.BirthDate));
        Assert.Contains("Paso 4 de 7", prompt); Assert.NotNull(choices);
    }

    [Fact]
    public void AKnownAnswerThatDoesNotFitIsAskedNotKept()
    {
        var (state, prompt, _) = Intake.Start(Today, "Rosa Sintética", "Prueba", "el año pasado", "female");

        Assert.Equal(3, state.Step); Assert.Null(state.Sex); // nothing after the answer that failed is taken either
        Assert.Contains("fecha de nacimiento", prompt);
    }

    [Fact]
    public async Task AgentHandsTheFormWhatItAlreadyKnows()
    {
        var everything = new { givenNames = "Irene Sintética", familyNames = "Mora Prueba", birthDate = "1979-09-09", sex = "female", emergencyContactName = "Sofía Sintética", emergencyContactRelationship = "hija", emergencyContactPhone = "70000007" };

        await h.Runtime(h.Model(AgentHarness.ToolCall("start_registration", everything), AgentHarness.Reply("Ok")), h.Sender()).Run(h.Job, CancellationToken.None);

        // Nothing is missing, so there is nothing to ask: the patient gets the card to confirm.
        var card = Assert.Single(h.Sent);
        Assert.Contains("*Irene Sintética Mora Prueba*", card); Assert.EndsWith("¿Están correctos?", card);
        Assert.Single(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id && a.Kind.StartsWith("proposal:")).ToListAsync());
    }

    [Fact]
    public async Task AMinorIsNoticedAsSoonAsTheBirthDateIsKnown()
    {
        var minor = new { givenNames = "Diego Sintético", familyNames = "Prueba", birthDate = "2010-01-01", sex = "male" };

        await h.Runtime(h.Model(AgentHarness.ToolCall("start_registration", minor), AgentHarness.Reply("Ok")), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Contains("menor de 18", Assert.Single(h.Sent));
        Assert.Contains(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).ToListAsync(), a => a.Kind == "handoff_offer");
        Assert.DoesNotContain(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).ToListAsync(), a => a.Kind == "intake");
    }

    [Fact]
    public async Task ARelationshipSaidInFrontOfTheContactsNameIsNotAskedAgain()
    {
        // Eval registro-105, three passes out of three with gpt-6-luna: «mi hija Sofía» reached the form as a name only, and the patient was asked the relationship.
        await h.Say("patient", "Mi hija Sofía Sintética, 70000007.");
        var said = new { givenNames = "Irene Sintética", familyNames = "Mora Prueba", birthDate = "9 de septiembre de 1979", sex = "femenino", emergencyContactName = "Sofía Sintética", emergencyContactPhone = "70000007" };

        await h.Runtime(h.Model(AgentHarness.ToolCall("start_registration", said), AgentHarness.Reply("Ok")), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.DoesNotContain("parentesco tiene", h.Sent[^1]);
        Assert.Contains("Sofía Sintética (hija)", h.Sent[^1]);
    }

    [Fact]
    public async Task AModelThatSaysNothingAfterStartingTheFormStillStartsIt()
    {
        // Found with gpt-6-luna: told not to write anything after start_registration, it wrote nothing, and the empty reply was
        // treated as a failure («no logré responder eso»). The earlier tests never saw it: their simulated model always wrote something.
        var silent = AgentHarness.Json(new { choices = new[] { new { message = new { role = "assistant", content = "" } } } });

        await h.Runtime(h.Model(AgentHarness.ToolCall("start_registration", new { givenNames = "Rosa Sintética" }), silent), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Contains("Paso 2 de 7", Assert.Single(h.Sent));
        Assert.DoesNotContain(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).ToListAsync(), a => a.Kind == "handoff_offer");
    }

    [Theory]
    [InlineData("Quiero registrar a mi papá para una cita, él no tiene WhatsApp. Se llama Pedro Sintético Prueba.")]
    [InlineData("Registre a mi esposa: Carla Sintética Prueba, 3 de marzo de 1991.")]
    [InlineData("Agéndele una cita a mi hijo mañana, por favor.")]
    public async Task ARequestForSomeoneElseNeverRegistersOrBooksUnderThisPhone(string message)
    {
        // Found with gpt-6-luna: «registra a mi papá» ended in a registration card for the father, tied to the sender's number.
        // His prescriptions would then be delivered to that phone.
        await h.Say("patient", message);
        var father = new { givenNames = "Pedro Sintético", familyNames = "Prueba", birthDate = "1950-02-02", sex = "male", emergencyContactName = "Julia Sintética", emergencyContactRelationship = "hija", emergencyContactPhone = "70000003" };
        var model = h.Model(AgentHarness.ToolCall("start_registration", father), AgentHarness.ToolCall("propose_registration", father), AgentHarness.Reply("Cada persona debe escribir desde su propio número."));

        await h.Runtime(model, h.Sender()).Run(h.Job, CancellationToken.None);

        var trail = await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).ToListAsync();
        Assert.DoesNotContain(trail, a => a.Kind.StartsWith("proposal:") || a.Kind == "intake");
        Assert.DoesNotContain(h.Sent, text => text.Contains("Pedro Sintético"));
    }
}
