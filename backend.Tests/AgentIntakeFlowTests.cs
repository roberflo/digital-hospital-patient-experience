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
}
