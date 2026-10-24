using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Recepcion;
using Xunit;

// MR-14: the webhook is driven through the RequestDelegate that Hosting.MapRecepcionApi really maps on
// POST /webhooks/kapso, with a body signed the way Rules.VerifySignature checks it. No copy of Receive.
public sealed class WhatsAppWebhookTests : IAsyncLifetime
{
    const string Secret = "synthetic-webhook-secret";
    readonly Guid tenant = Guid.NewGuid(), channel = Guid.NewGuid(); readonly string number = Guid.NewGuid().ToString("N");
    DbContextOptions<CrmDb> options = null!;
    static string Connection => Environment.GetEnvironmentVariable("TEST_DATABASE") ?? throw new InvalidOperationException("Use scripts/test-backend.sh");
    static readonly CapturedLogs Logs = new();
    // One built, never started, host for the class: EF caches CrmDb's model, and with it the first data
    // protection provider it saw, so a host disposed after one test would break the next one's encryption.
    static readonly Lazy<(WebApplication App, RequestDelegate Receive)> Host = new(() =>
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = Connection, ["Auth:Authority"] = "https://identity.example.test/realms/hospital", ["KEY_DIRECTORY"] = Directory.CreateTempSubdirectory().FullName, ["KAPSO_WEBHOOK_SECRET"] = Secret, ["PHONE_HASH_KEY"] = "synthetic-phone-key" });
        builder.Services.AddRecepcionServices(builder.Configuration, false); builder.Logging.AddProvider(Logs);
        var app = builder.Build(); app.MapRecepcionApi();
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>(), x => x.RoutePattern.RawText == "/webhooks/kapso");
        Assert.Contains("POST", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        return (app, endpoint.RequestDelegate!);
    });

    public async Task InitializeAsync()
    {
        options = new DbContextOptionsBuilder<CrmDb>().UseNpgsql(Connection).Options;
        await using var db = Db(); await db.Database.MigrateAsync();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "Synthetic tenant" });
        db.Channels.Add(new Channel { Id = channel, TenantId = tenant, Name = "Synthetic", PhoneNumberId = number, Enabled = true });
        await db.SaveChangesAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask;
    sealed class CapturedLogs : ILoggerProvider, ILogger
    {
        public readonly ConcurrentQueue<(LogLevel Level, string Text)> Entries = new();
        public ILogger CreateLogger(string category) => this; public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null; public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format) => Entries.Enqueue((level, format(state, ex) + " " + ex));
    }
    // Warnings this test's channel left; the channel id is in every one, so parallel tests never mix.
    string[] Warnings() => Logs.Entries.Where(x => x.Level == LogLevel.Warning && x.Text.Contains(channel.ToString())).Select(x => x.Text).ToArray();
    CrmDb Db() => new(options, new TenantScope { Id = tenant }, new EphemeralDataProtectionProvider());

    async Task<int> Post(string json, string? signature = null, string delivery = "delivery-1")
    {
        var body = Encoding.UTF8.GetBytes(json);
        using var services = Host.Value.App.Services.CreateScope();
        var ctx = new DefaultHttpContext { RequestServices = services.ServiceProvider };
        ctx.Request.Method = "POST"; ctx.Request.Path = "/webhooks/kapso"; ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(body); ctx.Request.ContentLength = body.Length; ctx.Response.Body = new MemoryStream();
        ctx.Request.Headers["X-Webhook-Signature"] = signature ?? Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body));
        ctx.Request.Headers["X-Webhook-Event"] = "whatsapp.message.received"; ctx.Request.Headers["X-Idempotency-Key"] = delivery;
        await Host.Value.Receive(ctx);
        return ctx.Response.StatusCode;
    }
    string Inbound(string? id, string phone = "50370000001") =>
        "{\"phone_number_id\":\"" + number + "\",\"message\":{" + (id is null ? "" : "\"id\":\"" + id + "\",") + "\"from\":\"" + phone + "\",\"type\":\"text\",\"text\":{\"body\":\"Hola sintético\"},\"kapso\":{\"direction\":\"inbound\"}},\"conversation\":{\"id\":\"conv-" + phone + "\",\"phone_number\":\"" + phone + "\",\"contact_name\":\"Paciente sintético\"}}";
    async Task<(int Messages, int Jobs, int Receipts, int Contacts, bool Touched)> Rows()
    {
        await using var db = Db();
        return (await db.Messages.CountAsync(), await db.Jobs.CountAsync(), await db.Receipts.CountAsync(), await db.Contacts.CountAsync(), await db.Channels.AnyAsync(x => x.LastWebhookAt != null));
    }

    [Fact] public async Task InvalidSignatureIs401AndWritesNothing()
    {
        Assert.Equal(401, await Post(Inbound("wamid.bad-signature"), signature: Convert.ToHexString(new byte[32])));
        Assert.Equal((0, 0, 0, 0, false), await Rows());
    }
    [Fact] public async Task SignedInboundCreatesOneMessageAndOneJobAndARedeliveryAddsNothing()
    {
        var json = Inbound("wamid.once");
        Assert.Equal(200, await Post(json));
        Assert.Equal((1, 1, 1, 1, true), await Rows());
        Assert.Equal(200, await Post(json)); // INV-6: Kapso redelivers the same event; no second message, job or receipt.
        Assert.Equal((1, 1, 1, 1, true), await Rows());
    }
    [Fact] public async Task ARedeliveryAfterTheReceiptIsSweptStillAddsNothing()
    {
        // MR-39: the receipt lives 30 days; past that, the message's ExternalId is what keeps a redelivery from becoming a second turn.
        var json = Inbound("wamid.swept");
        Assert.Equal(200, await Post(json));
        await using (var db = Db())
        {
            (await db.Receipts.SingleAsync()).CreatedAt = DateTimeOffset.UtcNow.AddDays(-31); await db.SaveChangesAsync();
            await Retention.Daily(db, tenant, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
            Assert.Empty(await db.Receipts.ToListAsync());
        }
        Assert.Equal(200, await Post(json, delivery: "delivery-2"));
        Assert.Equal((1, 1, 1, 1, true), await Rows());
    }
    [Theory]
    [InlineData("{\"phone_number_id\":")] // truncated
    [InlineData("not json")]
    [InlineData("{\"batch\":true,\"data\":5}")] // valid JSON, not a batch
    [InlineData("{\"batch\":true,\"data\":[1]}")] // a batch item that is not an object
    [InlineData("valid-then-1")] // a valid item first: nothing of it may be written either
    public async Task SignedMalformedJsonIs400AndWritesNothing(string json)
    {
        if (json == "valid-then-1") json = "{\"batch\":true,\"data\":[" + Inbound("wamid.before-bad") + ",1]}";
        Assert.Equal(400, await Post(json));
        Assert.Equal((0, 0, 0, 0, false), await Rows());
    }
    [Fact] public async Task MessageWithoutIdIsSkippedAndTheRestOfTheBatchIsProcessed()
    {
        var batch = "{\"batch\":true,\"data\":[" + Inbound(null, "50370000002") + "," + Inbound("wamid.after", "50370000003") + "]}";
        Assert.Equal(200, await Post(batch));
        await using var db = Db();
        Assert.Equal("wamid.after", Assert.Single(await db.Messages.Select(x => x.ExternalId).ToListAsync())); // projected: the body is sealed by the host's key ring
        Assert.Equal("agent:wamid.after", Assert.Single(await db.Jobs.Select(x => x.Key).ToListAsync()));
        // Both items are receipted, so a redelivery skips them; only the item with an id made a contact.
        Assert.Equal(2, await db.Receipts.CountAsync()); Assert.Equal(1, await db.Contacts.CountAsync());
        var warning = Assert.Single(Warnings()); Assert.Contains("id", warning); Assert.Contains(tenant.ToString(), warning);
        Assert.DoesNotContain("50370000002", warning); Assert.DoesNotContain("Hola", warning);
    }
    // An unusable phone is one bad item, not a bad delivery: it used to throw a 500 halfway, and every
    // redelivery lost the rest of the batch again.
    // Criterio 30: 8 digits are a Salvadoran number without its code everywhere, so a wa_id of 8 digits in all (+290, +500, part of +677) cannot be
    // told apart from one; it is an unusable phone: no contact, no conversation, nothing sent.
    [Fact] public async Task AnEightDigitWaIdIsUnusable()
    {
        Assert.Equal(200, await Post("{\"batch\":true,\"data\":[" + Inbound("wamid.e", "29022345") + "]}"));
        await using var db = Db();
        Assert.Equal((0, 0, 0), (await db.Contacts.CountAsync(), await db.Messages.CountAsync(), await db.Jobs.CountAsync()));
    }
    [Fact] public async Task InvalidPhoneIsSkippedAndTheRestOfTheBatchIsProcessed()
    {
        var batch = "{\"batch\":true,\"data\":[" + Inbound("wamid.a", "50370000004") + "," + Inbound("wamid.b", "999abc999") + "," + Inbound("wamid.c", "50370000005") + "]}";
        Assert.Equal(200, await Post(batch));
        Assert.Equal(200, await Post(batch));
        await using var db = Db();
        Assert.Equal(new[] { "wamid.a", "wamid.c" }, (await db.Messages.Select(x => x.ExternalId).ToListAsync()).Order());
        Assert.Equal(new[] { "agent:wamid.a", "agent:wamid.c" }, (await db.Jobs.Select(x => x.Key).ToListAsync()).Order());
        Assert.Equal(2, await db.Contacts.CountAsync()); Assert.Equal(3, await db.Receipts.CountAsync());
        var warning = Assert.Single(Warnings()); Assert.Contains(tenant.ToString(), warning);
        Assert.DoesNotContain("999abc999", warning); Assert.DoesNotContain("Hola", warning); Assert.DoesNotContain("wamid", warning);
    }
}
