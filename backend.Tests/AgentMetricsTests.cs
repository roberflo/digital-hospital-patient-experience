using Recepcion;
using Xunit;

/// <summary>docs/reception-agent.md, criterio 66: what the agent did, counted from its own trail.</summary>
public sealed class AgentMetricsTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();

    [Fact]
    public async Task CountsWhatTheAgentDidInThePeriodForThisHospital()
    {
        var other = new Contact { TenantId = h.Scope.Id, Name = "Otra Sintética", Phone = "50370000098", PhoneHash = Guid.NewGuid().ToString() }; h.Db.Add(other);
        var second = new Conversation { TenantId = h.Scope.Id, ChannelId = h.Conversation.ChannelId, ContactId = other.Id, Status = "human" }; h.Db.Add(second);
        Activity Step(Conversation conversation, string kind, string role = "agent_ai", int daysAgo = 1) => new() { TenantId = h.Scope.Id, ConversationId = conversation.Id, ContactId = conversation.ContactId, Kind = kind, Actor = "x", ActorRole = role, Body = "sintético", CreatedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo) };
        h.Db.Activities.AddRange(
            Step(h.Conversation, "response"), Step(h.Conversation, "appointment"), Step(h.Conversation, "appointment"), Step(h.Conversation, "prescription_delivered"), Step(h.Conversation, "patient_registered"),
            Step(second, "response"), Step(second, "handoff_offer"), Step(second, "handoff"), Step(second, "waiting_notice"), Step(second, "guard", "system"), Step(second, "agent_provider", "system"),
            Step(second, "handoff", "receptionist"), // a person's own handoff is not the agent's
            Step(h.Conversation, "handoff", daysAgo: 10)); // before the period
        await h.Db.SaveChangesAsync();

        var week = await AgentMetrics.Read(h.Db, 7, CancellationToken.None);

        Assert.Equal((2, 1, 1), (week.Attended, week.WithoutPerson, week.HandedOver));
        Assert.Equal((2, 1, 1), (week.Appointments, week.Registrations, week.Prescriptions));
        Assert.Equal((1, 1, 1, 1), (week.PersonOffers, week.Withheld, week.ProviderFailures, week.WaitingNotices));
        Assert.Equal(2, (await AgentMetrics.Read(h.Db, 30, CancellationToken.None)).HandedOver);
        Assert.Equal(90, (await AgentMetrics.Read(h.Db, 5000, CancellationToken.None)).Days);
    }
}
