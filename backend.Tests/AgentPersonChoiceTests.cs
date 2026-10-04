using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>docs/reception-agent.md, criterios 44–46: outside an emergency the patient decides whether to go to a person,
/// with a button. The agent explains and offers; it does not take the conversation away on its own.</summary>
public sealed class AgentPersonChoiceTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    static readonly AgentHarness.Fake NoModel = new(_ => throw new InvalidOperationException("The model must not be consulted"));
    List<string> Buttons() => h.Interactive[^1].GetProperty("action").GetProperty("buttons").EnumerateArray().Select(b => b.GetProperty("reply").GetProperty("id").GetString()!).ToList();
    Task<List<Activity>> Trail() => h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).OrderBy(a => a.CreatedAt).ToListAsync();

    [Fact]
    public async Task AgentThatWantsToHandOffAsksFirst()
    {
        var model = h.Model(AgentHarness.ToolCall("handoff", new { reason = "Pregunta por un cambio de dosis" }), AgentHarness.Reply("Te paso con el equipo."));

        await h.Runtime(model, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("agent", (await h.Fresh()).Status); // still the patient's conversation with the agent
        var sent = Assert.Single(h.Sent);
        Assert.Contains("¿Quieres que te pase con una persona?", sent);
        Assert.DoesNotContain("dosis", sent); // the internal reason is for the team, not for the chat
        Assert.Equal(["PERSONA", "MENU"], Buttons());
        Assert.DoesNotContain(await Trail(), a => a.Kind == "handoff");
    }

    [Theory]
    [InlineData("PERSONA")]
    [InlineData("Sí")]
    [InlineData("si por favor")]
    public async Task ChoosingAPersonHandsOffWithTheReasonTheTeamNeeds(string answer)
    {
        await h.Runtime(h.Model(AgentHarness.ToolCall("handoff", new { reason = "Pregunta por un cambio de dosis" }), AgentHarness.Reply("Ok")), h.Sender()).Run(h.Job, CancellationToken.None);
        await h.Say("patient", answer);

        await h.Runtime(NoModel, h.Sender()).Run(h.Job, CancellationToken.None);

        var conversation = await h.Fresh();
        Assert.Equal("human", conversation.Status);
        Assert.Equal("Pregunta por un cambio de dosis", (await Trail()).Last(a => a.Kind == "handoff").Body); // why the agent offered it
        Assert.StartsWith("Le pasé tu consulta al equipo del hospital", h.Sent[^1]);
    }

    [Fact]
    public async Task ChoosingToStayShowsTheMenu()
    {
        await h.Runtime(h.Model(AgentHarness.ToolCall("handoff", new { reason = "Consulta de precio" }), AgentHarness.Reply("Ok")), h.Sender()).Run(h.Job, CancellationToken.None);
        await h.Say("patient", "MENU");

        await h.Runtime(NoModel, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("agent", (await h.Fresh()).Status);
        Assert.Contains(AgentHarness.Options(h.Interactive[^1]), option => option.Id == "AGENDAR");
    }

    [Theory]
    [InlineData(true)]   // the model itself says it is urgent: symptoms, risk
    public async Task UrgentHandoffIsStillImmediate(bool urgent)
    {
        await h.Runtime(h.Model(AgentHarness.ToolCall("handoff", new { reason = "Dolor de pecho", urgent }), AgentHarness.Reply("Ok")), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("human", (await h.Fresh()).Status);
        Assert.StartsWith("Si es una emergencia", Assert.Single(h.Sent));
    }

    [Theory]
    [InlineData("Creo que tomé una sobredosis")]              // an emergency is never a question with buttons
    [InlineData("Quiero hablar con una persona, por favor")]  // the patient already decided
    public async Task EmergencyAndAnExplicitRequestAreStillImmediate(string message)
    {
        await h.Say("patient", message);

        await h.Runtime(NoModel, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("human", (await h.Fresh()).Status);
    }

    [Fact]
    public async Task AWithheldReplyBecomesAnOfferNotATransfer()
    {
        await h.Runtime(h.Model(AgentHarness.Reply("Toma 500 mg de amoxicilina cada 8 horas.")), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("agent", (await h.Fresh()).Status);
        Assert.DoesNotContain(h.Sent, text => text.Contains("500 mg")); // still withheld
        Assert.Equal(["PERSONA", "MENU"], Buttons());
        Assert.Contains(await Trail(), a => a.Kind == "guard");
    }

    [Fact]
    public async Task AFileTheAgentCannotReadIsOfferedToAPerson()
    {
        h.Db.Add(new Message { TenantId = h.Scope.Id, ConversationId = h.Conversation.Id, ExternalId = "file-" + Guid.NewGuid(), Body = "[Archivo recibido]", Sender = "patient", Type = "image" });
        await h.Db.SaveChangesAsync();
        var latest = await h.Db.Messages.OrderByDescending(m => m.CreatedAt).FirstAsync();
        var job = new Job { TenantId = h.Scope.Id, ConversationId = h.Conversation.Id, Key = "agent:" + latest.ExternalId }; h.Db.Add(job); await h.Db.SaveChangesAsync();

        await h.Runtime(NoModel, h.Sender()).Run(job, CancellationToken.None);

        Assert.Equal("agent", (await h.Fresh()).Status);
        Assert.Contains("archivo", Assert.Single(h.Sent));
        Assert.Equal(["PERSONA", "MENU"], Buttons());
    }
}
