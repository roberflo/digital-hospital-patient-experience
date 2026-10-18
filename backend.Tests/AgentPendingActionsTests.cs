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
    async Task Say(string text, HttpMessageHandler? model = null, HttpMessageHandler? hospital = null, Dictionary<string, string?>? extra = null) { await h.Say("patient", text); await h.Runtime(model ?? NoModel, h.Sender(), hospital, extra).Run(h.Job, CancellationToken.None); }
    Task<List<Activity>> Trail() => h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).ToListAsync();
    /// <summary>Someone rewrites the message the agent asked with: the flow must not notice.</summary>
    async Task RewriteLastAgentMessage()
    {
        var prompt = await h.Db.Messages.Where(m => m.ConversationId == h.Conversation.Id && m.Sender == "agent").OrderByDescending(m => m.CreatedAt).FirstAsync();
        prompt.Body = "Mensaje reescrito sin la pregunta."; await h.Db.SaveChangesAsync();
    }
    /// <summary>The conversation's first message, moved back so that what is seeded after it in the past still follows it.</summary>
    async Task Earlier(TimeSpan ago) { foreach (var m in await h.Db.Messages.Where(m => m.ConversationId == h.Conversation.Id).ToListAsync()) m.CreatedAt = DateTimeOffset.UtcNow - ago; await h.Db.SaveChangesAsync(); }
    async Task EmergencyLine() { (await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id)).EmergencyPhone = "2200 0000"; await h.Db.SaveChangesAsync(); }

    /// <summary>(b) Every way the agent asks, answered «sí» after the question's text was changed in the database.</summary>
    [Theory]
    [InlineData("person")]               // OfferPerson
    [InlineData("emergency-no-slots")]   // the turn's closing message, nothing free: the question with its two buttons
    [InlineData("emergency-list")]       // the turn's closing message with the list of hours
    [InlineData("emergency-symptoms")]   // the model told the symptoms apart: the server's question replaces the model's
    [InlineData("emergency-menu")]       // «Agendar cita» from the menu, nothing free in the next hours
    [InlineData("emergency-narrow")]     // the model's question kept beside the choice of doctor (the narrowing question)
    [InlineData("person-model")]         // the model asked, in the canonical words, whether to pass the patient to a person
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
            case "emergency-narrow": await Say("Me siento mal, quiero cita", h.Model(AgentHarness.ToolCall("hospital_availability", new { date = DateTime.UtcNow.AddHours(-6).AddDays(3).ToString("yyyy-MM-dd") }), AgentHarness.Reply("Siento que te sientas así. ¿Es una emergencia? Si no, te ayudo con la cita.")), h.Hospital(availability: TwoDoctors(DateTimeOffset.UtcNow.AddDays(3)))); break;
            case "person-model": await Say("¿Cuánto cuesta una limpieza?", h.Model(AgentHarness.Reply("El precio lo confirma recepción.\n\n" + AgentGuard.PersonQuestion))); break;
        }
        var person = path.StartsWith("person");
        Assert.Contains(person ? AgentGuard.PersonQuestion : AgentGuard.EmergencyQuestion, h.Sent[^1]); // the question went out: the rest measures something
        var asked = Assert.Single(await h.Pending("question"));
        Assert.Equal(person ? "person" : "emergency", asked.Code);
        Assert.Equal((await h.Db.Messages.Where(m => m.ConversationId == h.Conversation.Id && m.Sender == "agent").OrderByDescending(m => m.CreatedAt).FirstAsync()).Id, asked.PromptMessageId);

        await RewriteLastAgentMessage();
        await Say("Sí");

        Assert.Equal("human", (await h.Fresh()).Status);
        if (person) { Assert.StartsWith("Le pasé tu consulta", h.Sent[^1]); Assert.Equal(path == "person" ? "El contacto pide su receta y no tiene expediente vinculado." : "El paciente pidió hablar con una persona.", (await Trail()).Single(a => a.Kind == "handoff").Body); }
        else Assert.StartsWith("Si es una emergencia", h.Sent[^1]); // urgent: what to do now comes first
        Assert.Empty(await h.Pending("question")); // the answer used the question
    }

    static object TwoDoctors(DateTimeOffset at) => new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { Guid.NewGuid(), Guid.NewGuid() }.Select((id, i) => new { clinicianId = id, clinicianName = i == 0 ? "Dra. Sintética" : "Dr. Modelo", placeName = "", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "s" + i, startsAt = at.AddMinutes(30 * i), durationMinutes = 30, takenBy = 0, offered = true } } } } }).ToArray() };

    /// <summary>I1, the deploy: a question the agent sent before PendingActions existed has none. Within 24 hours of it, and only for a message
    /// the agent sent before the cut, its canonical words still say what it asked.</summary>
    [Theory]
    [InlineData(AgentGuard.EmergencyQuestion, 1, true)]     // sent before the cut: «sí» is an emergency
    [InlineData(AgentGuard.EmergencyQuestion, -1, false)]   // sent after the cut with no PendingAction: the words alone are not the question
    [InlineData(AgentGuard.PersonQuestion, 1, true)]
    [InlineData(AgentGuard.PersonQuestion, -1, false)]
    public async Task AQuestionSentBeforeTheDeployIsStillAnswered(string question, int cutAfterMinutes, bool derives)
    {
        await EmergencyLine(); await Earlier(TimeSpan.FromMinutes(60));
        var sent = await h.Say("agent", "Mensaje anterior al despliegue.\n\n" + question, DateTimeOffset.UtcNow.AddMinutes(-30));
        var extra = new Dictionary<string, string?> { ["AGENT_LEGACY_QUESTIONS_BEFORE"] = sent.CreatedAt.AddMinutes(cutAfterMinutes).ToString("O") };

        await Say("Sí", h.Model(AgentHarness.Reply("Claro, dime en qué te ayudo.")), extra: extra);

        Assert.Equal(derives ? "human" : "agent", (await h.Fresh()).Status);
        if (derives && question == AgentGuard.EmergencyQuestion) Assert.StartsWith("Si es una emergencia", h.Sent[^1]);
        if (derives && question == AgentGuard.PersonQuestion) Assert.StartsWith("Le pasé tu consulta", h.Sent[^1]);
    }

    [Fact]
    public async Task AQuestionSentBeforeTheDeployIsOnlyAnsweredWithinTheDay()
    {
        await EmergencyLine(); await Earlier(TimeSpan.FromHours(26));
        var sent = await h.Say("agent", AgentGuard.EmergencyQuestion, DateTimeOffset.UtcNow.AddHours(-25));

        await Say("Sí", h.Model(AgentHarness.Reply("Claro, dime en qué te ayudo.")), extra: new() { ["AGENT_LEGACY_QUESTIONS_BEFORE"] = sent.CreatedAt.AddMinutes(1).ToString("O") });

        Assert.Equal("agent", (await h.Fresh()).Status);
    }

    /// <summary>Nit1: a question that never went out (no message stamped) answers nothing, even right after another patient message.</summary>
    [Fact]
    public async Task AQuestionThatNeverWentOutIsNotAnswered()
    {
        h.Db.PendingActions.Add(new PendingAction { TenantId = h.Scope.Id, ConversationId = h.Conversation.Id, Kind = "question", Code = "person", Body = "Motivo sintético", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) }); await h.Db.SaveChangesAsync();

        await Say("Sí", h.Model(AgentHarness.Reply("Claro, dime en qué te ayudo."))); // the message before it is the patient's own

        Assert.Equal("agent", (await h.Fresh()).Status);
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

    /// <summary>A form that expired no longer waits on its question: «Emergencia: …» after it is read as the patient wrote it (MR-40, N1).</summary>
    [Fact]
    public async Task AnExpiredFormNoLongerLabelsTheEmergencyWord()
    {
        await Say(Tap(new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero)));
        foreach (var answer in Answers[..4]) await Say(answer);
        Assert.Contains("contacto de emergencia", h.Sent[^1]);
        var form = Assert.Single(await h.Pending("intake")); form.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); await h.Db.SaveChangesAsync();

        await Say("Emergencia: Carlos Sintético");

        Assert.Equal("human", (await h.Fresh()).Status);
    }

    /// <summary>MR-35 + MR-40: the model asked for the emergency contact in its own words; the question is recorded as it goes out, so
    /// «Emergencia: …» in the answer labels that field whatever the message says later. The same words from the team do not.</summary>
    [Theory]
    [InlineData(true, "agent")]
    [InlineData(false, "human")]
    public async Task TheModelsQuestionForTheEmergencyContactLabelsTheAnswer(bool byTheAgent, string status)
    {
        var question = "Gracias. ¿Quién es tu contacto de emergencia? Nombre, parentesco y teléfono.";
        if (byTheAgent) { await Say("Quiero registrarme", h.Model(AgentHarness.Reply(question))); Assert.Single(await h.Pending("question"), q => q.Code == "emergency-contact"); await RewriteLastAgentMessage(); }
        else await h.Say("human", question);

        await Say("Emergencia: María López", h.Model(AgentHarness.Reply("Gracias. ¿Me das su teléfono?")));

        Assert.Equal(status, (await h.Fresh()).Status);
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
        using var used = System.Text.Json.JsonDocument.Parse(Assert.Single(await Trail(), a => a.Kind == "proposal_used:" + card.Code).Body);
        Assert.Equal(("create", start), (used.RootElement.GetProperty("action").GetString(), used.RootElement.GetProperty("startsAt").GetDateTimeOffset())); // the history says which appointment was confirmed
    }
}
