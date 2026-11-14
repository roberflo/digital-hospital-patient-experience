using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Recepcion;
using Recepcion.Integrations;
using Xunit;

/// <summary>A01 S1 (docs/a01-recuperar-mensajes.md, criterios 1–15): what the webhook did not deliver, recovered from Kapso for a person.
/// Kapso's list answer is the shape of the docs.kapso.ai «List messages» example (read 2026-11-14), SIN CONFIRMAR against a real number (G0).</summary>
public sealed class MessageRecoveryTests : IAsyncLifetime
{
    const string HashKey = "synthetic-phone-key", Secret = "synthetic-webhook-secret", Phone = "50370000000";
    readonly AgentHarness h = new();
    readonly List<Uri> asked = []; int posts;
    Func<HttpRequestMessage, Task<HttpResponseMessage>> answer = _ => Task.FromResult(Page([]));
    readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    string number = "";

    public async Task InitializeAsync()
    {
        await h.Start(); h.Contact.PhoneHash = Rules.PhoneHash(Phone, HashKey); await h.Db.SaveChangesAsync();
        number = (await h.Db.Channels.SingleAsync()).PhoneNumberId;
    }
    public Task DisposeAsync() => h.DisposeAsync().AsTask();

    // ---- doubles ----
    /// <summary>One listed message as the documented example has it: Meta's message plus the kapso() extension.</summary>
    internal static object Item(string? id, DateTimeOffset? at, string body = "Hola recuperado", string phone = Phone, string direction = "inbound", string? origin = null, string status = "received", string? timestamp = null) => new
    {
        id, from = phone, timestamp = timestamp ?? at?.ToUnixTimeSeconds().ToString(), type = "text", text = new { body },
        kapso = new { direction, status, processing_status = "processed", phone_number = phone, has_media = false, whatsapp_conversation_id = "wconv-" + phone, contact_name = "Paciente sintético", content = body, origin }
    };
    internal static HttpResponseMessage Page(object[] items, string? after = null) => AgentHarness.Json(new { data = items, paging = new { cursors = new { after } } });
    /// <summary>Kapso double: GET …/messages is the list (recorded), anything else a send (counted).</summary>
    AgentHarness.Fake Kapso() => new(async r =>
    {
        if (r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/messages")) { asked.Add(r.RequestUri); return await answer(r); }
        posts++; return AgentHarness.KapsoSent("out-" + Guid.NewGuid(), Phone);
    });
    IConfiguration Config(bool enabled = true) => h.Config(new() { ["PHONE_HASH_KEY"] = HashKey, ["KAPSO_RECOVERY_ENABLED"] = enabled ? "true" : null });
    Task Recover(DateTimeOffset at, HttpMessageHandler? kapso = null, ILogger? log = null, int pages = MessageRecovery.PagesPerPass, AgentHarness? on = null, TimeSpan? budget = null, TimeSpan? timeout = null) =>
        MessageRecovery.Run((on ?? h).Db, (on ?? h).Scope, new KapsoClient(new HttpClient(kapso ?? Kapso()) { Timeout = timeout ?? TimeSpan.FromSeconds(100) }, Config()), Config(), log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None, at, pages, budget);
    /// <summary>What AgentWorker.RunTenant resolves, with the recovery's client and configuration.</summary>
    IServiceProvider Worker(HttpClient list, AgentHarness.Fake sender, bool enabled = true, HttpMessageHandler? hospital = null)
    {
        var config = Config(enabled);
        var runtime = h.Runtime(h.Model(AgentHarness.Reply("Respuesta sintética")), sender, hospital, new() { ["PHONE_HASH_KEY"] = HashKey });
        return new ServiceCollection().AddSingleton(h.Scope).AddSingleton(h.Db).AddSingleton(runtime)
            .AddSingleton(new ConversationService(h.Db, h.Scope, new KapsoClient(new HttpClient(sender), config)))
            .AddSingleton(new KapsoClient(list, config)).AddSingleton(config).BuildServiceProvider();
    }
    static string Query(Uri uri, string key) => HttpUtility.ParseQueryString(uri.Query)[key] ?? "";
    static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    Task<List<Message>> Saved(string? id = null) => h.Db.Messages.AsNoTracking().Where(x => id == null ? x.ExternalId!.StartsWith("wamid.rec") : x.ExternalId == id).OrderBy(x => x.CreatedAt).ToListAsync();
    Task<List<Activity>> Handoffs() => h.Db.Activities.AsNoTracking().Where(x => x.Kind == "handoff").ToListAsync();
    sealed class Answering(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => handler(r, ct); }
    sealed class Lines : ILogger
    {
        public readonly List<string> All = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null; public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format) => All.Add(level + " " + format(state, ex) + " " + ex);
    }

    /// <summary>A signed delivery through WhatsAppEndpoints.Receive itself (not a copy), on a context of <paramref name="on"/>'s key ring: the mapped
    /// host of WhatsAppWebhookTests has its own, and could not read the rows the harness encrypted.</summary>
    async Task Webhook(string channelNumber, object item, string phone = Phone, AgentHarness? on = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { phone_number_id = channelNumber, message = item, conversation = new { id = "conv-" + phone, phone_number = phone, contact_name = "Paciente sintético" } });
        var ctx = new DefaultHttpContext(); ctx.Request.Method = "POST"; ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(body); ctx.Request.ContentLength = body.Length;
        ctx.Request.Headers["X-Webhook-Signature"] = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body));
        ctx.Request.Headers["X-Webhook-Event"] = "whatsapp.message.received"; ctx.Request.Headers["X-Idempotency-Key"] = "delivery-" + Guid.NewGuid();
        var scope = new TenantScope(); await using var db = (on ?? h).NewDb(scope);
        var config = h.Config(new() { ["PHONE_HASH_KEY"] = HashKey, ["KAPSO_WEBHOOK_SECRET"] = Secret });
        Assert.Equal(200, ((IStatusCodeHttpResult)await WhatsAppEndpoints.Receive(ctx, db, scope, config, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)).StatusCode);
    }

    // ---- criterios ----
    [Fact] public async Task C1_the_client_lists_inbound_messages_between_two_instants_through_every_page()
    {
        var calls = new List<HttpRequestMessage>();
        var fake = new AgentHarness.Fake(r => { calls.Add(r); return Task.FromResult(calls.Count == 1 ? Page([Item("wamid.a", Now)], after: "cursor-2") : Page([Item("wamid.b", Now)])); });
        var kapso = new KapsoClient(new HttpClient(fake), Config());
        var pages = new List<(JsonElement[] Items, string? After)>();
        await foreach (var page in kapso.Inbound(number, Now.AddHours(-1), Now)) pages.Add(page);

        Assert.Equal(2, calls.Count); Assert.Equal([1, 1], pages.Select(p => p.Items.Length)); Assert.Equal(["cursor-2", null], pages.Select(p => p.After));
        var first = calls[0].RequestUri!;
        Assert.Equal("api.kapso.ai", first.Host); Assert.Equal($"/meta/whatsapp/v24.0/{number}/messages", first.AbsolutePath);
        Assert.Equal(("inbound", Iso(Now.AddHours(-1)), Iso(Now), "100", ""), (Query(first, "direction"), Query(first, "since"), Query(first, "until"), Query(first, "limit"), Query(first, "after")));
        Assert.Equal("kapso()", Query(first, "fields"));
        Assert.Equal("synthetic", Assert.Single(calls[0].Headers.GetValues("X-API-Key")));
        Assert.Equal("cursor-2", Query(calls[1].RequestUri!, "after"));

        var failing = new KapsoClient(new HttpClient(new AgentHarness.Fake(_ => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)))), Config());
        await Assert.ThrowsAsync<HttpRequestException>(async () => { await foreach (var _ in failing.Inbound(number, Now.AddHours(-1), Now)) { } });
    }

    [Fact] public async Task C2_a_recovered_message_is_stored_as_the_webhook_would_have_stored_it()
    {
        await using var webhook = new AgentHarness(); await webhook.Start();
        var item = Item("wamid.rec-same", Now.AddMinutes(-10), "Necesito una cita", phone: "50370000009");
        await Webhook((await webhook.Db.Channels.SingleAsync()).PhoneNumberId, item, phone: "50370000009", on: webhook);
        answer = _ => Task.FromResult(Page([item]));
        await Recover(Now);

        var hooked = await webhook.Db.Messages.AsNoTracking().SingleAsync(x => x.ExternalId == "wamid.rec-same");
        var recovered = Assert.Single(await Saved("wamid.rec-same"));
        Assert.Equal((hooked.Body, hooked.Type, hooked.MediaId, hooked.Sender, hooked.ExternalId, hooked.CreatedAt), (recovered.Body, recovered.Type, recovered.MediaId, recovered.Sender, recovered.ExternalId, recovered.CreatedAt));
        Assert.Equal(("Necesito una cita", Now.AddMinutes(-10), false), (recovered.Body, recovered.CreatedAt, recovered.IsHistory));
        var contact = await h.Db.Contacts.AsNoTracking().SingleAsync(x => x.PhoneHash == Rules.PhoneHash("50370000009", HashKey));
        Assert.Equal(contact.Id, await h.Db.Conversations.AsNoTracking().Where(x => x.Id == recovered.ConversationId).Select(x => x.ContactId).SingleAsync());
    }

    [Fact] public async Task C3_recovering_twice_does_not_duplicate()
    {
        answer = _ => Task.FromResult(Page([Item("wamid.rec-twice", Now.AddMinutes(-10))]));
        await Recover(Now); var revision = (await h.Fresh()).Revision;
        await Recover(Now + MessageRecovery.Every);

        Assert.Single(await Saved("wamid.rec-twice")); Assert.Single(await Handoffs());
        Assert.Equal(revision, (await h.Fresh()).Revision); Assert.Equal(2, asked.Count);
    }

    [Fact] public async Task C4a_recovery_then_the_webhook_leaves_one_message_and_no_job()
    {
        var item = Item("wamid.rec-first", Now.AddMinutes(-10)); var jobs = await h.Db.Jobs.CountAsync();
        answer = _ => Task.FromResult(Page([item]));
        await Recover(Now); await Webhook(number, item);

        Assert.Single(await Saved("wamid.rec-first"));
        Assert.Equal(jobs, await h.Db.Jobs.AsNoTracking().CountAsync()); Assert.False(await h.Db.Jobs.AnyAsync(x => x.Key == "agent:wamid.rec-first"));
    }

    [Fact] public async Task C4b_the_webhook_then_recovery_leaves_one_message_its_job_and_the_status()
    {
        var item = Item("wamid.rec-late", Now.AddMinutes(-10));
        await Webhook(number, item); var status = (await h.Fresh()).Status;
        // The listing reports it read since: recovery stores, it never reconciles a status (Ingest's «recovered is null» guards).
        answer = _ => Task.FromResult(Page([Item("wamid.rec-late", Now.AddMinutes(-10), status: "read")]));
        await Recover(Now);

        Assert.Equal("received", Assert.Single(await Saved("wamid.rec-late")).Status);
        Assert.Equal("agent:wamid.rec-late", Assert.Single(await h.Db.Jobs.AsNoTracking().Where(x => x.Key == "agent:wamid.rec-late").Select(x => x.Key).ToListAsync()));
        Assert.Equal("agent", status); Assert.Equal(status, (await h.Fresh()).Status); Assert.Empty(await Handoffs());
    }

    [Fact] public async Task C5_recovery_never_wakes_the_agent()
    {
        // The harness's pending Job answers its own live message of this conversation; it must not answer with, or because of, the recovered one.
        var kapso = Kapso(); answer = _ => Task.FromResult(Page([Item("wamid.rec-quiet", Now.AddMinutes(-10), "Texto recuperado")]));
        await Recover(Now, kapso);
        Assert.Equal([h.Job.Id], await h.Db.Jobs.AsNoTracking().Select(x => x.Id).ToListAsync());

        await AgentWorker.RunTenant(Worker(new HttpClient(kapso), kapso), h.Scope.Id, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        Assert.Equal(0, posts); Assert.Single(await Saved("wamid.rec-quiet"));
        Assert.Equal("done", await h.Db.Jobs.AsNoTracking().Where(x => x.Id == h.Job.Id).Select(x => x.Status).SingleAsync());
    }

    [Theory][InlineData("agent")][InlineData("closed")][InlineData("human")]
    public async Task C6_the_conversation_goes_to_a_person_and_says_so_once_per_pass(string from)
    {
        h.Conversation.Status = from; h.Conversation.State = from == "closed" ? "resolved" : "open"; h.Conversation.AssignedTo = null; await h.Db.SaveChangesAsync();
        answer = _ => Task.FromResult(Page([Item("wamid.rec-1", Now.AddMinutes(-12)), Item("wamid.rec-2", Now.AddMinutes(-11)), Item("wamid.rec-3", Now.AddMinutes(-10))]));
        await Recover(Now);

        var c = await h.Fresh(); Assert.Equal(("human", "open"), (c.Status, c.State));
        // Already with a person (amended 2026-11-14, I-1): no handoff, which would end a wait the agent's handoff opened (criterios 62–64); a «recovered» note says it.
        var said = Assert.Single(await h.Db.Activities.AsNoTracking().Where(x => x.Kind == "handoff" || x.Kind == "recovered").ToListAsync());
        Assert.Equal((from == "human" ? "recovered" : "handoff", "system", MessageRecovery.Note, h.Conversation.Id), (said.Kind, said.ActorRole, said.Body, said.ConversationId));
        Assert.Equal(3, (await Saved()).Count);
    }

    [Fact] public async Task C8_the_conversation_keeps_its_real_order()
    {
        var live = await h.Db.Messages.AsNoTracking().SingleAsync();
        h.Conversation.LastMessage = live.Body; h.Conversation.LastInboundAt = live.CreatedAt; await h.Db.SaveChangesAsync();
        answer = _ => Task.FromResult(Page([Item("wamid.rec-b", Now.AddHours(-2).AddMinutes(5), "Segundo"), Item("wamid.rec-a", Now.AddHours(-2), "Primero")]));
        await Recover(Now);

        Assert.Equal(["Primero", "Segundo", live.Body], (await WhatsAppEndpoints.Messages(h.Conversation.Id, h.Db))!.Select(x => x.Body));
        var c = await h.Fresh(); Assert.Equal((live.Body, live.CreatedAt), (c.LastMessage, c.LastInboundAt));
    }

    [Fact] public async Task C9_recovery_does_not_get_ahead_of_the_webhook()
    {
        var young = Item("wamid.rec-young", Now.AddMinutes(-1));
        answer = _ => Task.FromResult(Page([young]));
        await Recover(Now);

        Assert.Equal(Iso(Now - MessageRecovery.Grace), Query(Assert.Single(asked), "until"));
        Assert.Empty(await Saved("wamid.rec-young"));
        await Webhook(number, young);
        Assert.Single(await Saved("wamid.rec-young")); Assert.True(await h.Db.Jobs.AnyAsync(x => x.Key == "agent:wamid.rec-young"));
    }

    [Fact] public async Task C10_recovery_never_looks_past_the_cap()
    {
        answer = _ => Task.FromResult(Page([Item("wamid.rec-old", Now.AddHours(-25)), Item("wamid.rec-in", Now.AddHours(-23))]));
        await Recover(Now);
        await Recover(Now + MessageRecovery.Every);

        Assert.Equal(Iso(Now - MessageRecovery.Cap), Query(asked[0], "since"));
        Assert.Equal(Iso(Now - MessageRecovery.Grace - MessageRecovery.Grace), Query(asked[1], "since"));
        Assert.Equal(["wamid.rec-in"], (await Saved()).Select(x => x.ExternalId));
    }

    [Fact] public async Task C11_a_large_window_is_completed_not_truncated()
    {
        var all = Enumerable.Range(0, 250).Select(i => Item($"wamid.rec-{i:000}", Now.AddHours(-3).AddSeconds(i))).ToArray();
        answer = r => Task.FromResult(Query(r.RequestUri!, "after") switch
        {
            "" => Page(all[..100], "p2"), "p2" => Page(all[100..200], "p3"), _ => Page(all[200..])
        });
        await Recover(Now, pages: 2);
        Assert.Equal(200, (await Saved()).Count);
        await Recover(Now + MessageRecovery.Every, pages: 2);

        Assert.Equal(250, (await Saved()).Count);
        Assert.Equal(3, asked.Count); Assert.Equal("p3", Query(asked[2], "after"));
        // The stopped pass did not move the window: the same since, until and Kapso's cursor (M-1); the Cap is kept at save time (INV-6).
        Assert.Equal((Query(asked[0], "since"), Query(asked[0], "until")), (Query(asked[2], "since"), Query(asked[2], "until")));
    }

    [Fact] public async Task M2_a_timestamp_out_of_range_is_skipped_not_the_end_of_the_pass()
    {
        // Milliseconds, as some APIs send them: Ingest's own range check, never a throw that stalls the channel.
        answer = _ => Task.FromResult(Page([Item("wamid.rec-millis", null, timestamp: Now.AddMinutes(-10).ToUnixTimeMilliseconds().ToString()), Item("wamid.rec-fine", Now.AddMinutes(-10))]));
        await Recover(Now);
        await Recover(Now + MessageRecovery.Every);

        Assert.Equal(["wamid.rec-fine"], (await Saved()).Select(x => x.ExternalId));
        Assert.Equal(Iso(Now - MessageRecovery.Grace - MessageRecovery.Grace), Query(asked[1], "since")); // the pass completed
    }

    [Fact] public async Task M4_a_hung_Kapso_holds_the_tenant_for_the_budget_at_most()
    {
        var log = new Lines(); var hung = new Answering(async (r, ct) => { asked.Add(r.RequestUri!); await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Recover(Now, hung, log, budget: TimeSpan.FromMilliseconds(300), timeout: Timeout.InfiniteTimeSpan).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), clock.Elapsed.ToString());
        Assert.Contains(log.All, x => x.StartsWith("Warning Message recovery failed") && x.Contains(h.Scope.Id.ToString()));
        answer = _ => Task.FromResult(Page([]));
        await Recover(Now + MessageRecovery.Every);
        Assert.Equal(Iso(Now + MessageRecovery.Every - MessageRecovery.Cap), Query(asked[1], "since")); // the cut pass left no cursor: the whole Cap again, not until − Grace
    }

    [Fact] public async Task C12_a_recovered_message_touches_neither_consent_nor_appointments()
    {
        h.Db.Jobs.Remove(h.Job); await h.Link(); h.Contact.ReminderConsentAt = Now.AddDays(-3); await h.Db.SaveChangesAsync();
        var reminder = new AppointmentReminder { TenantId = h.Scope.Id, ContactId = h.Contact.Id, PatientId = h.Contact.PatientId!.Value, ChannelId = h.Conversation.ChannelId, AppointmentId = Guid.NewGuid(), StartsAt = Now.AddDays(1), DueAt = Now.AddHours(-1), Window = "day_before", Status = "sent", AttemptedAt = Now.AddHours(-1) };
        h.Db.Add(reminder); await h.Db.SaveChangesAsync();
        var confirm = new { id = "wamid.rec-confirm", from = Phone, timestamp = Now.AddMinutes(-9).ToUnixTimeSeconds().ToString(), type = "button", button = new { payload = "ASISTO " + reminder.Id, text = "Confirmar" },
            kapso = new { direction = "inbound", phone_number = Phone, contact_name = "Paciente sintético", content = "Confirmar" } };
        var kapso = Kapso(); answer = _ => Task.FromResult(Page([Item("wamid.rec-baja", Now.AddMinutes(-10), "BAJA"), confirm]));
        await Recover(Now, kapso);
        var hospital = new AgentHarness.Fake(_ => Task.FromResult(AgentHarness.Json(new { })));
        await AgentWorker.RunTenant(Worker(new HttpClient(kapso), kapso, hospital: hospital), h.Scope.Id, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

        Assert.Equal(2, (await Saved()).Count);
        Assert.NotNull((await h.Db.Contacts.AsNoTracking().SingleAsync(x => x.Id == h.Contact.Id)).ReminderConsentAt);
        Assert.False(await h.Db.Activities.AnyAsync(x => x.Kind == "reminder_consent"));
        var after = await h.Db.AppointmentReminders.AsNoTracking().SingleAsync(x => x.Id == reminder.Id);
        Assert.Equal((reminder.Status, reminder.StartsAt, reminder.AttemptedAt), (after.Status, after.StartsAt, after.AttemptedAt));
        Assert.Equal(0, hospital.Calls); Assert.Equal(0, posts);
    }

    [Theory][InlineData("500")][InlineData("timeout")]
    public async Task C13_when_Kapso_fails_the_agent_goes_on_and_the_cursor_stays(string failure)
    {
        var start = DateTimeOffset.UtcNow - MessageRecovery.Every - TimeSpan.FromMinutes(1);
        await Recover(start); // a pass that worked: the next asks from its until minus the overlap
        var expected = Query(Assert.Single(asked), "until"); var mode = failure;
        var list = new Answering(async (r, ct) =>
        {
            asked.Add(r.RequestUri!);
            if (mode == "timeout") await Task.Delay(Timeout.Infinite, ct); // until HttpClient.Timeout cancels it, as a hung Kapso
            return mode == "ok" ? Page([]) : new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
        });
        var log = new Lines(); var sender = h.Sender();
        await AgentWorker.RunTenant(Worker(new HttpClient(list) { Timeout = TimeSpan.FromMilliseconds(300) }, sender), h.Scope.Id, log, CancellationToken.None);

        Assert.Equal("done", await h.Db.Jobs.AsNoTracking().Where(x => x.Id == h.Job.Id).Select(x => x.Status).SingleAsync());
        Assert.Equal(2, asked.Count);
        Assert.Contains(log.All, x => x.StartsWith("Warning Message recovery failed " + (failure == "500" ? "HttpRequestException" : "TaskCanceledException")) && x.Contains(h.Scope.Id.ToString()));
        mode = "ok";
        await MessageRecovery.Run(h.Db, h.Scope, new KapsoClient(new HttpClient(list), Config()), Config(), log, CancellationToken.None, DateTimeOffset.UtcNow + MessageRecovery.Every + TimeSpan.FromMinutes(1));
        Assert.Equal(3, asked.Count);
        Assert.Equal(Query(asked[1], "since"), Query(asked[2], "since"));
        Assert.Equal(Iso(DateTimeOffset.Parse(expected) - MessageRecovery.Grace), Query(asked[2], "since"));
    }

    [Fact] public async Task C14_the_log_carries_no_patient_data()
    {
        var log = new Lines(); const string body = "Me duele el pecho desde anoche";
        answer = _ => Task.FromResult(Page([Item("wamid.rec-private", Now.AddMinutes(-10), body)]));
        await Recover(Now, log: log);
        answer = _ => throw new HttpRequestException("Synthetic failure for 50370000000 wamid.rec-private");
        await Recover(Now + MessageRecovery.Every, log: log);

        Assert.Single(await Saved("wamid.rec-private"));
        Assert.Equal(2, log.All.Count(x => x.Contains(h.Scope.Id.ToString()) && x.Contains(h.Conversation.ChannelId.ToString())));
        Assert.Contains(log.All, x => x.Contains("1 items"));
        foreach (var line in log.All) { Assert.DoesNotContain(Phone, line); Assert.DoesNotContain(body, line); Assert.DoesNotContain("wamid", line); }
    }

    [Fact] public async Task C15_off_does_nothing()
    {
        var kapso = Kapso(); var sender = h.Sender();
        await AgentWorker.RunTenant(Worker(new HttpClient(kapso), sender, enabled: false), h.Scope.Id, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        Assert.Equal(0, kapso.Calls);
        await AgentWorker.RunTenant(Worker(new HttpClient(kapso), sender), h.Scope.Id, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        Assert.Equal(1, kapso.Calls); // the same tenant, switched on: the pass runs
    }

    [Fact] public async Task C15_a_demo_channel_is_never_asked()
    {
        (await h.Db.Channels.SingleAsync()).PhoneNumberId = "demo-" + Guid.NewGuid().ToString("N"); await h.Db.SaveChangesAsync();
        var kapso = Kapso(); await Recover(Now, kapso);
        Assert.Equal(0, kapso.Calls);
    }

    [Fact] public async Task C15_each_tenant_only_its_own_and_only_what_the_patient_sent()
    {
        await using var other = new AgentHarness(); await other.Start();
        async Task<int> Rows() { await using var db = other.NewContext(); return await db.Messages.IgnoreQueryFilters().CountAsync(x => x.TenantId == other.Scope.Id) + await db.Contacts.IgnoreQueryFilters().CountAsync(x => x.TenantId == other.Scope.Id)
            + await db.Conversations.IgnoreQueryFilters().CountAsync(x => x.TenantId == other.Scope.Id) + await db.Activities.IgnoreQueryFilters().CountAsync(x => x.TenantId == other.Scope.Id); }
        var before = await Rows();
        answer = _ => Task.FromResult(Page([Item("wamid.rec-in", Now.AddMinutes(-10)), Item("wamid.rec-echo", Now.AddMinutes(-10), direction: "outbound"), Item("wamid.rec-history", Now.AddMinutes(-10), origin: "history_sync"), Item(null, Now.AddMinutes(-10)), Item("wamid.rec-untimed", null)]));
        await Recover(Now);

        Assert.Equal(["wamid.rec-in"], (await Saved()).Select(x => x.ExternalId));
        Assert.Equal(before, await Rows());
        var theirs = (await other.Db.Channels.SingleAsync()).PhoneNumberId; Assert.DoesNotContain(asked, x => x.AbsolutePath.Contains(theirs));
    }
}
