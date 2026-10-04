using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>docs/reception-agent.md, criterios 62–64: after the agent hands a conversation to the team and until a person
/// answers, the patient is not left in silence. No model is consulted while waiting. Synthetic people only.</summary>
public sealed class AgentWaitingTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    static readonly AgentHarness.Fake NoModel = new(_ => throw new InvalidOperationException("The model must not be consulted"));
    public async Task InitializeAsync()
    {
        await h.Start(message: "PERSONA"); await h.Link();
        var tenant = await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id); tenant.EmergencyPhone = "2200 0000"; await h.Db.SaveChangesAsync();
        await Run(); h.Sent.Clear(); h.Interactive.Clear(); // the agent handed the conversation to the team
    }
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    Task Run() => h.Runtime(NoModel, h.Sender(), h.Hospital()).Run(h.Job, CancellationToken.None);
    async Task Say(string text) { await h.Say("patient", text); await Run(); }
    /// <summary>Moves everything so far into the past, as if the patient had been waiting that long.</summary>
    async Task Wait(TimeSpan time)
    {
        foreach (var message in await h.Db.Messages.Where(x => x.ConversationId == h.Conversation.Id).ToListAsync()) message.CreatedAt -= time;
        foreach (var step in await h.Db.Activities.Where(x => x.ConversationId == h.Conversation.Id).ToListAsync()) step.CreatedAt -= time;
        await h.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task ATapIsStillServedWhileThePatientWaitsForAPerson()
    {
        Assert.Equal("human", (await h.Fresh()).Status);

        await Say("MISCITAS");

        Assert.Contains("No tienes citas", Assert.Single(h.Sent));
        Assert.Equal("human", (await h.Fresh()).Status); // still in the team's inbox
    }

    [Fact]
    public async Task FreeTextAfterTenMinutesGetsOneNoticeAndRaisesThePriority()
    {
        await Wait(TimeSpan.FromMinutes(11));

        await Say("¿Hola? ¿Hay alguien?");

        var notice = Assert.Single(h.Sent);
        Assert.Contains("sigue con el equipo", notice); Assert.Contains("2200 0000", notice);
        Assert.Equal(["AGENDAR", "MISCITAS", "RECETA"], AgentHarness.Options(h.Interactive[^1]).Select(o => o.Id));
        var conversation = await h.Fresh(); Assert.Equal("human", conversation.Status); Assert.Equal("high", conversation.Priority);
        Assert.Single(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id && a.Kind == "waiting_notice").ToListAsync());

        await Say("Sigo esperando.");
        Assert.Single(h.Sent); // said once, not on every message
    }

    [Fact]
    public async Task FreeTextRightAfterTheHandoffIsLeftForThePerson()
    {
        await Say("Es sobre el resultado de mi examen.");

        Assert.Empty(h.Sent);
        Assert.Equal("normal", (await h.Fresh()).Priority);
    }

    [Fact]
    public async Task OnceAPersonAnswersTheAgentStaysSilent()
    {
        await h.Say("human", "Hola, soy de recepción. Ya reviso tu consulta."); await Wait(TimeSpan.FromMinutes(30));

        await Say("MISCITAS"); await Say("¿Sigues ahí?");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task AConversationThePeopleTookIsNeverAnswered()
    {
        h.Db.Activities.Add(new Activity { TenantId = h.Scope.Id, ConversationId = h.Conversation.Id, ContactId = h.Contact.Id, Kind = "handoff", Actor = "Recepcionista Sintética", ActorRole = "receptionist", ActorSubject = "staff-1", Body = "Atención humana activada." });
        await h.Db.SaveChangesAsync(); await Wait(TimeSpan.FromMinutes(30));

        await Say("MISCITAS"); await Say("¿Hay alguien?");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task AnEmergencyWhileWaitingStillGetsThePhone()
    {
        await Say("Creo que tomé una sobredosis de mis pastillas.");

        var message = Assert.Single(h.Sent);
        Assert.Contains("emergencia", message); Assert.Contains("2200 0000", message);
        Assert.Equal("human", (await h.Fresh()).Status);
    }
}
