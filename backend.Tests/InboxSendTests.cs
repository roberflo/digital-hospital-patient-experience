using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Recepcion;
using Recepcion.Integrations;
using Xunit;

/// <summary>MR-36: a reply from /inbox goes through the same handler MapWhatsApp registers for POST /api/conversations/{id}/messages.</summary>
public sealed class InboxSendTests
{
    /// <summary>WhatsApp double that records the number each message is addressed to.</summary>
    static AgentHarness.Fake Recipients(List<string> to) => new(async request =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        to.Add(body.RootElement.GetProperty("to").GetString()!);
        return AgentHarness.KapsoSent("out-" + Guid.NewGuid(), to[^1].TrimStart('+'));
    });
    static ConversationService Service(AgentHarness h, AgentHarness.Fake sender, bool manual) =>
        new(h.Db, h.Scope, new KapsoClient(new HttpClient(sender), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["KAPSO_API_KEY"] = "synthetic", ["KAPSO_MANUAL_SEND_ENABLED"] = manual ? "true" : null }).Build()));
    static Task<IResult> Post(AgentHarness h, ConversationService svc, string key, string body = "Hola, le escribe recepción")
    {
        var ctx = new DefaultHttpContext(); ctx.Request.Headers["Idempotency-Key"] = key;
        return WhatsAppEndpoints.SendMessage(h.Conversation.Id, new SendInput(body), ctx, h.Db, h.Scope, new CurrentUser { Subject = "staff-1", Role = "reception", Name = "Recepción Sintética" }, svc);
    }

    [Fact]
    public async Task Send_pauses_the_agent_leaves_an_activity_and_one_key_sends_once()
    {
        await using var h = new AgentHarness(phone: "+503 7000-0000");
        await h.Start();
        var to = new List<string>(); var sender = Recipients(to); var svc = Service(h, sender, manual: true);
        const string key = "11111111-1111-4111-8111-111111111111";

        var first = Assert.IsAssignableFrom<IValueHttpResult>(await Post(h, svc, key)).Value as Message;
        var second = Assert.IsAssignableFrom<IValueHttpResult>(await Post(h, svc, key)).Value as Message;

        Assert.NotNull(first); Assert.Equal(first.Id, second!.Id); Assert.Equal("sent", first.Status);
        Assert.Equal(["+50370000000"], to); // INV-9, criterio 28: «+» and the country code
        Assert.Equal(1, await h.Db.Messages.CountAsync(x => x.ConversationId == h.Conversation.Id && x.Sender == "human"));
        var conversation = await h.Fresh();
        Assert.Equal("human", conversation.Status); Assert.Equal("staff-1", conversation.AssignedTo);
        var activity = await h.Db.Activities.SingleAsync(x => x.ConversationId == h.Conversation.Id && x.Kind == "response");
        Assert.Equal(first.Id, activity.MessageId); Assert.Equal("staff-1", activity.ActorSubject);
    }

    /// <summary>Criterio 27: a contact stored with 8 digits, before 503 was assumed, is written to with its country code.</summary>
    [Fact]
    public async Task An_old_eight_digit_contact_is_sent_with_its_country_code()
    {
        await using var h = new AgentHarness(phone: "70000001");
        await h.Start();
        var to = new List<string>(); var svc = Service(h, Recipients(to), manual: true);

        await Post(h, svc, "22222222-2222-4222-8222-222222222222");

        Assert.Equal(["+50370000001"], to);
        Assert.Equal("70000001", (await h.Db.Contacts.AsNoTracking().SingleAsync(c => c.Id == h.Contact.Id)).Phone); // read, never rewritten
    }

    /// <summary>A refused send leaves the conversation as it was: the agent keeps it, nobody is assigned, no trace of a reply.</summary>
    [Theory]
    [InlineData("window")]
    [InlineData("paused")]
    [InlineData("channel")]
    public async Task Refused_send_changes_nothing(string reason)
    {
        await using var h = new AgentHarness();
        await h.Start();
        if (reason == "window") h.Conversation.LastInboundAt = DateTimeOffset.UtcNow.AddDays(-2);
        if (reason == "channel") (await h.Db.Channels.SingleAsync(x => x.Id == h.Conversation.ChannelId)).Enabled = false;
        h.Conversation.State = "pending"; await h.Db.SaveChangesAsync();
        var revision = (await h.Fresh()).Revision;
        var sender = Recipients([]); var svc = Service(h, sender, manual: reason != "paused");

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Post(h, svc, "22222222-2222-4222-8222-222222222222"));

        Assert.Contains(reason switch { "window" => "Ventana", "paused" => "pausa", _ => "desactivado" }, error.Message);
        var conversation = await h.Fresh();
        Assert.Equal(("agent", (string?)null, "pending", revision), (conversation.Status, conversation.AssignedTo, conversation.State, conversation.Revision));
        Assert.Equal(0, sender.Calls);
        Assert.False(await h.Db.Messages.AnyAsync(x => x.ConversationId == h.Conversation.Id && x.Sender == "human"));
        Assert.False(await h.Db.Activities.AnyAsync(x => x.ConversationId == h.Conversation.Id));
    }

    [Fact]
    public async Task Empty_body_or_missing_key_changes_nothing()
    {
        await using var h = new AgentHarness();
        await h.Start();
        var svc = Service(h, Recipients([]), manual: true);
        await Assert.ThrowsAsync<ArgumentException>(() => Post(h, svc, "33333333-3333-4333-8333-333333333333", body: " "));
        await Assert.ThrowsAsync<ArgumentException>(() => Post(h, svc, ""));
        var conversation = await h.Fresh();
        Assert.Equal(("agent", (string?)null), (conversation.Status, conversation.AssignedTo));
    }
    [Fact]
    public async Task A_file_retried_with_its_key_is_not_uploaded_again()
    {
        // Se perdió la respuesta y la persona vuelve a pulsar: el envío ya existe, el paciente no recibe el archivo dos veces.
        await using var h = new AgentHarness();
        await h.Start();
        var sent = new Message { TenantId = h.Scope.Id, ConversationId = h.Conversation.Id, Sender = "human", Body = "receta.pdf", Type = "document", RequestKey = "file-" + Guid.NewGuid(), Status = "sent" };
        h.Db.Add(sent); await h.Db.SaveChangesAsync();
        var sender = h.Sender(); var kapso = new KapsoClient(new HttpClient(sender), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["KAPSO_API_KEY"] = "synthetic", ["KAPSO_MANUAL_SEND_ENABLED"] = "true" }).Build());
        var ctx = new DefaultHttpContext(); ctx.Request.Headers["Idempotency-Key"] = sent.RequestKey; var pdf = new MemoryStream("%PDF-synthetic"u8.ToArray());
        ctx.Request.Form = new FormCollection([], new FormFileCollection { new FormFile(pdf, 0, pdf.Length, "file", "receta.pdf") { Headers = new HeaderDictionary(), ContentType = "application/pdf" } });
        var result = await WhatsAppEndpoints.SendMedia(h.Conversation.Id, ctx, h.Db, h.Scope, new CurrentUser { Subject = "staff-1", Role = "reception", Name = "Recepción Sintética" }, kapso, new ConversationService(h.Db, h.Scope, kapso));
        Assert.Equal(sent.Id, Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<Message>>(result).Value!.Id);
        Assert.Equal(0, sender.Calls); // nothing uploaded
    }
    [Fact]
    public async Task A_file_without_key_changes_nothing()
    {
        // Antes la subida pausaba al agente y asignaba la conversación, y sólo después respondía 400 por la clave.
        await using var h = new AgentHarness();
        await h.Start();
        var sender = h.Sender(); var kapso = new KapsoClient(new HttpClient(sender), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["KAPSO_API_KEY"] = "synthetic", ["KAPSO_MANUAL_SEND_ENABLED"] = "true" }).Build());
        var ctx = new DefaultHttpContext(); var pdf = new MemoryStream("%PDF-synthetic"u8.ToArray());
        ctx.Request.Form = new FormCollection([], new FormFileCollection { new FormFile(pdf, 0, pdf.Length, "file", "receta.pdf") { Headers = new HeaderDictionary(), ContentType = "application/pdf" } });

        await Assert.ThrowsAsync<ArgumentException>(() => WhatsAppEndpoints.SendMedia(h.Conversation.Id, ctx, h.Db, h.Scope, new CurrentUser { Subject = "staff-1", Role = "reception", Name = "Recepción Sintética" }, kapso, new ConversationService(h.Db, h.Scope, kapso)));

        var conversation = await h.Fresh();
        Assert.Equal(("agent", (string?)null), (conversation.Status, conversation.AssignedTo));
        Assert.Equal(0, sender.Calls); // nothing uploaded
    }
}
