using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Recepcion;
using Recepcion.Integrations;
using Xunit;

/// <summary>Lote F (docs/mejoras-revision-2026-10.md, MR-46…MR-53): what GET /api/conversations, /overview and
/// the attention PATCH publish to the inbox, through the handlers MapWhatsApp and MapCrm register.</summary>
public sealed class InboxListTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    static readonly CurrentUser Staff = new() { Subject = "staff-1", Role = "reception", Name = "Recepción Sintética" };
    static KapsoClient Kapso(bool automatic = true, bool manual = true) => new(new HttpClient(new AgentHarness.Fake(_ => throw new InvalidOperationException("Nothing is sent"))),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["KAPSO_API_KEY"] = "synthetic", ["SEND_ENABLED"] = automatic ? "true" : null, ["KAPSO_MANUAL_SEND_ENABLED"] = manual ? "true" : null }).Build());
    ConversationService Service(KapsoClient? kapso = null) => new(h.Db, h.Scope, kapso ?? Kapso());
    Task<List<InboxRow>> List(string? status = null, string? q = null, ConversationService? svc = null, AgentHarness? on = null) =>
        WhatsAppEndpoints.List((on ?? h).Db, (on ?? h).Scope, Staff, new ConfigurationBuilder().Build(), svc ?? new ConversationService((on ?? h).Db, (on ?? h).Scope, Kapso()), null, null, status, null, "all", null, null, null, null, null, q, null);
    async Task AgentEnabled(bool on) { (await h.Db.Tenants.SingleAsync(x => x.Id == h.Scope.Id)).AgentEnabled = on; await h.Db.SaveChangesAsync(); }

    [Fact]
    public async Task MR46_with_the_agent_off_an_agent_conversation_waits_for_the_team()
    {
        await AgentEnabled(false);

        Assert.Empty(await List(status: "agent"));
        Assert.Equal(h.Conversation.Id, Assert.Single(await List(status: "human")).Conversation.Id);
        var overview = await CrmEndpoints.Overview(h.Db, h.Scope);
        Assert.Equal((1, 0), (overview.Human, overview.Agent));
        Assert.Equal("agent", (await h.Fresh()).Status); // shown differently, never rewritten (F-1)
    }

    [Fact]
    public async Task MR46_with_the_agent_on_nothing_changes()
    {
        Assert.Equal(h.Conversation.Id, Assert.Single(await List(status: "agent")).Conversation.Id);
        Assert.Empty(await List(status: "human"));
        var overview = await CrmEndpoints.Overview(h.Db, h.Scope);
        Assert.Equal((0, 1), (overview.Human, overview.Agent));
    }

    [Fact]
    public async Task MR47_the_agents_handoff_is_published_until_a_person_answers()
    {
        h.Conversation.Status = "human";
        h.Db.Activities.Add(new Activity { TenantId = h.Scope.Id, ConversationId = h.Conversation.Id, ContactId = h.Contact.Id, Kind = "handoff", Actor = "Agente", ActorRole = "agent_ai", Body = "Pregunta por un cambio de dosis" });
        await h.Db.SaveChangesAsync();

        Assert.Equal("Pregunta por un cambio de dosis", Assert.Single(await List()).Handoff?.Reason);

        await h.Say("human", "Hola, le escribe recepción");
        Assert.Null(Assert.Single(await List()).Handoff);
    }

    [Fact]
    public async Task MR47_a_person_taking_the_conversation_ends_the_handoff()
    {
        h.Conversation.Status = "human";
        h.Db.Activities.Add(new Activity { TenantId = h.Scope.Id, ConversationId = h.Conversation.Id, ContactId = h.Contact.Id, Kind = "handoff", Actor = "Agente", ActorRole = "agent_ai", Body = "Motivo sintético", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        h.Db.Activities.Add(new Activity { TenantId = h.Scope.Id, ConversationId = h.Conversation.Id, ContactId = h.Contact.Id, Kind = "handoff", Actor = "Recepción Sintética", ActorRole = "reception", Body = "Recepción Sintética tomó la conversación." });
        await h.Db.SaveChangesAsync();

        Assert.Null(Assert.Single(await List()).Handoff);
    }

    [Theory]
    [InlineData("channel")]
    [InlineData("window")]
    [InlineData("paused")]
    public async Task MR48_the_inbox_reads_the_same_refusal_the_send_would_throw(string reason)
    {
        if (reason == "window") h.Conversation.LastInboundAt = DateTimeOffset.UtcNow.AddDays(-2);
        if (reason == "channel") (await h.Db.Channels.SingleAsync(x => x.Id == h.Conversation.ChannelId)).Enabled = false;
        h.Conversation.Status = "human"; await h.Db.SaveChangesAsync();
        var svc = Service(Kapso(manual: reason != "paused", automatic: reason != "paused"));

        var row = Assert.Single(await List(svc: svc));

        var thrown = await Assert.ThrowsAsync<ArgumentException>(() => svc.RequireSendable(h.Conversation, manual: true));
        Assert.Equal((reason, thrown.Message), (row.SendBlock?.Code, row.SendBlock?.Reason));
        Assert.DoesNotContain("Kapso", thrown.Message);
        // F-2: a closed window does not stop the agent, the patient's next message reopens it.
        Assert.Equal(reason == "window" ? null : reason, row.AgentBlock?.Code);
    }

    [Fact]
    public async Task MR48_an_open_conversation_can_be_answered()
    {
        var row = Assert.Single(await List());
        Assert.Null(row.SendBlock); Assert.Null(row.AgentBlock);
    }

    [Theory]
    [InlineData("paciente sintetico")] // name, without accents or case
    [InlineData("7000")]               // digits of the phone
    [InlineData("rece")]               // a conversation label
    public async Task MR53_search_finds_by_name_phone_or_label_on_the_server(string q)
    {
        h.Conversation.Labels = "receta"; await h.Db.SaveChangesAsync();
        Assert.Equal(h.Conversation.Id, Assert.Single(await List(q: q)).Conversation.Id);
        Assert.Empty(await List(q: "nadie-coincide"));
    }

    [Fact]
    public async Task MR53_MR52_search_and_labels_stay_inside_the_tenant()
    {
        await using var other = new AgentHarness(phone: "50370000000");
        await other.Start();
        other.Conversation.Labels = "secreta"; h.Conversation.Labels = "cita, receta"; await other.Db.SaveChangesAsync(); await h.Db.SaveChangesAsync();

        Assert.Equal(h.Conversation.Id, Assert.Single(await List(q: "Paciente")).Conversation.Id);
        Assert.Empty(await List(q: "secreta"));
        Assert.Equal(["cita", "receta"], await WhatsAppEndpoints.Labels(h.Db));
    }

    [Fact]
    public async Task MR49_a_person_taking_the_conversation_is_named_in_its_activity()
    {
        h.Db.Members.Add(new Member { TenantId = h.Scope.Id, Subject = Staff.Subject, Name = Staff.Name, Role = "reception" }); await h.Db.SaveChangesAsync();
        var result = await WhatsAppEndpoints.SetAttention(h.Conversation.Id, new ConversationInput("human", Staff.Subject), h.Db, Staff, h.Scope, Service());

        Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IValueHttpResult>(result);
        var activity = await h.Db.Activities.AsNoTracking().SingleAsync(a => a.ConversationId == h.Conversation.Id && a.Kind == "handoff");
        Assert.Equal("Recepción Sintética tomó la conversación.", activity.Body);
    }
}
