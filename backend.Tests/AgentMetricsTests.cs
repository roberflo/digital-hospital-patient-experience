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
    /// <summary>Criterio 80: what reminders resolved, from their rows and the trail, only for this hospital.</summary>
    [Fact]
    public async Task CountsWhatRemindersResolvedInThePeriodForThisHospital()
    {
        AppointmentReminder Reminder(Guid tenant, string status, int daysAgo = 1, string window = "day_before") => new() { TenantId = tenant, PatientId = Guid.NewGuid(), AppointmentId = Guid.NewGuid(), Window = window, Status = status, StartsAt = DateTimeOffset.UtcNow.AddDays(1), DueAt = DateTimeOffset.UtcNow, AttemptedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo) };
        h.Db.AppointmentReminders.AddRange(Reminder(h.Scope.Id, "sent"), Reminder(h.Scope.Id, "sent", window: "hour_before"), Reminder(h.Scope.Id, "confirmed"), Reminder(h.Scope.Id, "confirmed"),
            Reminder(h.Scope.Id, "failed"), Reminder(h.Scope.Id, "pending"), Reminder(h.Scope.Id, "confirmed", daysAgo: 10));
        h.Db.Activities.Add(new() { TenantId = h.Scope.Id, ConversationId = h.Conversation.Id, ContactId = h.Contact.Id, Kind = "reminder_change_request", Actor = "Recordatorios", ActorRole = "system", Body = "sintético" });
        await h.Db.SaveChangesAsync();
        await using var other = new AgentHarness(); await other.Start();
        other.Db.AppointmentReminders.Add(Reminder(other.Scope.Id, "confirmed")); await other.Db.SaveChangesAsync();

        var week = await AgentMetrics.Read(h.Db, 7, CancellationToken.None);

        Assert.Equal((4, 2, 1), (week.RemindersSent, week.RemindersConfirmed, week.ChangeRequests));
    }

    /// <summary>Criterio 81: the median minutes from a patient's first message in the period to the first agent or person message after it.</summary>
    [Fact]
    public async Task FirstReplyIsTheMedianMinutesToTheFirstAgentOrPersonMessage()
    {
        Assert.Null((await AgentMetrics.Read(h.Db, 7, CancellationToken.None)).FirstReplyMinutes); // the harness's patient message is unanswered
        var start = DateTimeOffset.UtcNow.AddDays(-2);
        async Task Conversation(params (string Sender, int Minute, string Type, bool History)[] messages)
        {
            var contact = new Contact { TenantId = h.Scope.Id, Name = "Sintética", Phone = "5037000" + Random.Shared.Next(1000, 9999), PhoneHash = Guid.NewGuid().ToString() };
            var conversation = new Conversation { TenantId = h.Scope.Id, ChannelId = h.Conversation.ChannelId, ContactId = contact.Id, Status = "human" }; h.Db.AddRange(contact, conversation);
            foreach (var (sender, minute, type, history) in messages)
                h.Db.Add(new Message { TenantId = h.Scope.Id, ConversationId = conversation.Id, Sender = sender, Type = type, IsHistory = history, Body = "sintético", CreatedAt = start.AddMinutes(minute) });
            await h.Db.SaveChangesAsync();
        }
        await Conversation(("patient", 0, "text", false), ("patient", 1, "text", false), ("agent", 2, "text", false));          // 2
        await Conversation(("patient", 0, "text", false), ("system", 1, "template", false), ("human", 30, "text", false));     // 30: a template is not a reply
        await Conversation(("human", -5, "text", true), ("patient", 0, "text", false), ("system", 3, "text", false), ("human", 10, "text", false)); // 10
        await Conversation(("patient", -60 * 24 * 10, "text", false), ("agent", -60 * 24 * 10 + 1, "text", false));            // before the period

        Assert.Equal(10, (await AgentMetrics.Read(h.Db, 7, CancellationToken.None)).FirstReplyMinutes);
    }
}
