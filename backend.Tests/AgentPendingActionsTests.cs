using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>MR-40: what the agent waits on (a proposal, the registration form, the question just asked) lives in PendingActions and is matched
/// to the message that asked, never to that message's text; Activity is history and is never deleted. Synthetic people only.</summary>
public sealed class AgentPendingActionsTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    static readonly AgentHarness.Fake NoModel = new(_ => throw new InvalidOperationException("The model must not be consulted"));
    static readonly Guid Doctor = Guid.NewGuid();
    static readonly string[] Answers = ["Ana Sintética", "López Prueba", "12/03/1990", "Femenino", "Carlos Sintético", "Hermano", "7000 0001"];
    static string Today => DateTime.UtcNow.AddHours(-6).ToString("yyyy-MM-dd");
    static object Agenda(params DateTimeOffset[] starts) => new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = Doctor, clinicianName = "Dra. Sintética", placeName = "", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = starts.Select((start, i) => new { slotId = "s" + i, startsAt = start, durationMinutes = 30, takenBy = 0, offered = true }).ToArray() } } } } };
    static string Tap(DateTimeOffset start) => $"CITA {start.ToOffset(TimeSpan.FromHours(-6)):yyyy-MM-ddTHH:mm:sszzz} {Doctor} 30 Dra. Sintética";
    async Task Say(string text, HttpMessageHandler? model = null, HttpMessageHandler? hospital = null) { await h.Say("patient", text); await h.Runtime(model ?? NoModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None); }
    Task<List<Activity>> Trail() => h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).ToListAsync();
    /// <summary>Someone rewrites the message the agent asked with: the flow must not notice.</summary>
    async Task RewriteLastAgentMessage()
    {
        var prompt = await h.Db.Messages.Where(m => m.ConversationId == h.Conversation.Id && m.Sender == "agent").OrderByDescending(m => m.CreatedAt).FirstAsync();
        prompt.Body = "Mensaje reescrito sin la pregunta."; await h.Db.SaveChangesAsync();
    }
    async Task EmergencyLine() { (await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id)).EmergencyPhone = "2200 0000"; await h.Db.SaveChangesAsync(); }

    /// <summary>(b) Every way the agent asks, answered «sí» after the question's text was changed in the database.</summary>
    [Theory]
    [InlineData("person")]               // OfferPerson
    [InlineData("emergency-no-slots")]   // the turn's closing message, nothing free: the question with its two buttons
    [InlineData("emergency-list")]       // the turn's closing message with the list of hours
    [InlineData("emergency-symptoms")]   // the model told the symptoms apart: the server's question replaces the model's
    [InlineData("emergency-menu")]       // «Agendar cita» from the menu, nothing free in the next hours
    public async Task TheAnswerFollowsTheQuestionAskedNotTheTextOfTheMessage(string path)
    {
        await EmergencyLine();
        var later = DateTimeOffset.UtcNow.AddHours(30);
        switch (path)
        {
            case "person": await Say("RECETA"); break;
            case "emergency-no-slots": await Say("Quiero una cita hoy", h.Model(AgentHarness.ToolCall("hospital_availability", new { date = Today }), AgentHarness.Reply("Hoy no hay horarios.")), h.Hospital(availability: Agenda())); break;
            case "emergency-list": await Say("Quiero una cita hoy", h.Model(AgentHarness.ToolCall("hospital_availability", new { date = Today }), AgentHarness.Reply("Esto es lo que encontré.")), h.Hospital(availability: Agenda(later))); break;
            case "emergency-symptoms": await Say("Me siento muy mal del estómago", h.Model(AgentHarness.Reply("Siento que te sientas así. ¿Es una emergencia?"))); break;
            case "emergency-menu": await Say("AGENDAR", hospital: h.Hospital(availability: Agenda(later))); break;
        }
        var person = path == "person";
        Assert.Contains(person ? AgentGuard.PersonQuestion : AgentGuard.EmergencyQuestion, h.Sent[^1]); // the question went out: the rest measures something
        var asked = Assert.Single(await h.Pending("question"));
        Assert.Equal(person ? "person" : "emergency", asked.Code);
        Assert.Equal((await h.Db.Messages.Where(m => m.ConversationId == h.Conversation.Id && m.Sender == "agent").OrderByDescending(m => m.CreatedAt).FirstAsync()).Id, asked.PromptMessageId);

        await RewriteLastAgentMessage();
        await Say("Sí");

        Assert.Equal("human", (await h.Fresh()).Status);
        if (person) { Assert.StartsWith("Le pasé tu consulta", h.Sent[^1]); Assert.Equal("El contacto pide su receta y no tiene expediente vinculado.", (await Trail()).Single(a => a.Kind == "handoff").Body); }
        else Assert.StartsWith("Si es una emergencia", h.Sent[^1]); // urgent: what to do now comes first
        Assert.Empty(await h.Pending("question")); // the answer used the question
    }

    /// <summary>(c) A message of the team with the very same words asks nothing on the agent's behalf.</summary>
    [Theory]
    [InlineData(AgentGuard.PersonQuestion)]
    [InlineData("No tengo horarios en las próximas 8 horas. " + AgentGuard.EmergencyQuestion)]
    public async Task TheSameWordsFromTheTeamAreNotTheAgentsQuestion(string text)
    {
        await EmergencyLine();
        await h.Say("human", text);

        await Say("Sí", h.Model(AgentHarness.Reply("Claro, dime en qué te ayudo.")));

        Assert.Equal("agent", (await h.Fresh()).Status);
        Assert.DoesNotContain(await Trail(), a => a.Kind == "handoff");
    }

    /// <summary>(d) The form and the card come and go; the history does not.</summary>
    [Fact]
    public async Task NoActivityIsEverDeleted()
    {
        var seen = new HashSet<Guid>();
        async Task Kept() { var now = (await Trail()).Select(a => a.Id).ToHashSet(); Assert.Superset(seen, now); seen.UnionWith(now); }
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero);

        await Say(Tap(start)); await Kept();
        Assert.Single(await h.Pending("intake"));
        foreach (var answer in Answers) { await Say(answer); await Kept(); }
        var card = Assert.Single(await h.Pending("proposal"));
        await Say("CORREGIR " + card.Code); await Kept();                       // the card goes, the form starts over
        Assert.Empty(await h.Pending("proposal")); Assert.Single(await h.Pending("intake"));
        await Say("salir", h.Model(AgentHarness.Reply("De acuerdo, aquí sigo."))); await Kept(); // the form goes
        Assert.Empty(await h.Pending("intake"));

        Assert.DoesNotContain(await Trail(), a => a.Kind == "intake" || a.Kind == "visit_kind" || a.Kind.StartsWith("proposal:"));
    }

    /// <summary>(e) «Emergencia: …» answers the form's question about the emergency contact, even when that question's text changed.</summary>
    [Fact]
    public async Task TheFormsEmergencyContactIsNotAnEmergencyWhateverTheTextSays()
    {
        await Say(Tap(new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero)));
        foreach (var answer in Answers[..4]) await Say(answer);
        Assert.Contains("contacto de emergencia", h.Sent[^1]);

        await RewriteLastAgentMessage();
        await Say("Emergencia: Carlos Sintético");

        Assert.Equal("agent", (await h.Fresh()).Status);
        Assert.DoesNotContain(await Trail(), a => a.Kind.StartsWith("handoff"));
        Assert.Contains("contacto de emergencia", h.Sent[^1]); Assert.Single(await h.Pending("intake")); // the form goes on: it asks for the name again
    }

    /// <summary>INV-6: the proposal is consumed, and its data dropped, before anything is written to Hospital.</summary>
    [Fact]
    public async Task AProposalIsConsumedBeforeTheWriteAndKeepsNothing()
    {
        await h.Link();
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero);
        var inner = new HttpMessageInvoker(h.Hospital(availability: Agenda(start)));
        bool? consumedFirst = null;
        var hospital = new AgentHarness.Fake(async request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/agenda")
            {
                await using var other = h.With(); var row = await other.PendingActions.AsNoTracking().SingleAsync(x => x.ConversationId == h.Conversation.Id && x.Kind == "proposal");
                consumedFirst = row.ConsumedAt is not null && row.Body.Length == 0;
            }
            return await inner.SendAsync(request, CancellationToken.None);
        });

        await Say("Quiero una cita", h.Model(AgentHarness.ToolCall("propose_action", new { action = "create", doctorId = Doctor, startsAt = start, durationMinutes = 30 }), AgentHarness.Reply("Listo.")), hospital);
        var card = Assert.Single(await h.Pending("proposal"));
        Assert.Contains("startsAt", card.Body);
        Assert.Equal((await h.Db.Messages.Where(m => m.ConversationId == h.Conversation.Id && m.Sender == "agent").OrderByDescending(m => m.CreatedAt).FirstAsync()).Id, card.PromptMessageId);
        Assert.DoesNotContain(await Trail(), a => a.Kind.StartsWith("proposal:"));

        await RewriteLastAgentMessage();
        await Say("Sí", hospital: hospital); // «sí» confirms the card it answers, by the message, whatever its text

        Assert.Equal(["/v1/agenda"], h.HospitalWrites);
        Assert.True(consumedFirst);
        Assert.Empty(await h.Pending("proposal"));
        Assert.Single(await Trail(), a => a.Kind == "proposal_used:" + card.Code);
    }
}
