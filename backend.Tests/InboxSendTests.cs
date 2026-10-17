using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Recepcion;
using Recepcion.Integrations;
using Xunit;

/// <summary>MR-36: a reply from /inbox goes through the same handler MapWhatsApp registers for POST /api/conversations/{id}/messages.</summary>
public sealed class InboxSendTests
{
    [Fact]
    public async Task Send_pauses_the_agent_leaves_an_activity_and_one_key_sends_once()
    {
        await using var h = new AgentHarness();
        await h.Start();
        var sender = h.Sender();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["KAPSO_API_KEY"] = "synthetic", ["KAPSO_MANUAL_SEND_ENABLED"] = "true" }).Build();
        var svc = new ConversationService(h.Db, h.Scope, new KapsoClient(new HttpClient(sender), config));
        var user = new CurrentUser { Subject = "staff-1", Role = "reception", Name = "Recepción Sintética" };
        async Task<IResult> Post()
        {
            var ctx = new DefaultHttpContext(); ctx.Request.Headers["Idempotency-Key"] = "inbox-11111111-1111-4111-8111-111111111111";
            return await WhatsAppEndpoints.SendMessage(h.Conversation.Id, new SendInput("Hola, le escribe recepción"), ctx, h.Db, h.Scope, user, svc);
        }

        var first = Assert.IsAssignableFrom<IValueHttpResult>(await Post()).Value as Message;
        var second = Assert.IsAssignableFrom<IValueHttpResult>(await Post()).Value as Message;

        Assert.NotNull(first); Assert.Equal(first.Id, second!.Id); Assert.Equal("sent", first.Status);
        Assert.Equal(["Hola, le escribe recepción"], h.Sent);
        Assert.Equal(1, await h.Db.Messages.CountAsync(x => x.ConversationId == h.Conversation.Id && x.Sender == "human"));
        var conversation = await h.Fresh();
        Assert.Equal("human", conversation.Status); Assert.Equal("staff-1", conversation.AssignedTo);
        var activity = await h.Db.Activities.SingleAsync(x => x.ConversationId == h.Conversation.Id && x.Kind == "response");
        Assert.Equal(first.Id, activity.MessageId); Assert.Equal("staff-1", activity.ActorSubject);
    }

    [Fact]
    public async Task Manual_send_off_by_default_refuses_without_calling_Kapso()
    {
        await using var h = new AgentHarness();
        await h.Start();
        var sender = h.Sender();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["KAPSO_API_KEY"] = "synthetic" }).Build();
        var svc = new ConversationService(h.Db, h.Scope, new KapsoClient(new HttpClient(sender), config));
        var ctx = new DefaultHttpContext(); ctx.Request.Headers["Idempotency-Key"] = "inbox-22222222-2222-4222-8222-222222222222";
        var error = await Assert.ThrowsAsync<ArgumentException>(() => WhatsAppEndpoints.SendMessage(h.Conversation.Id, new SendInput("Hola"), ctx, h.Db, h.Scope, new CurrentUser { Subject = "staff-1", Role = "reception" }, svc));
        Assert.Contains("pausa", error.Message);
        Assert.Equal(0, sender.Calls);
        Assert.False(await h.Db.Messages.AnyAsync(x => x.ConversationId == h.Conversation.Id && x.Sender == "human"));
    }
}
