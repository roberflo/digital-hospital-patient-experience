using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Recepcion;
using Xunit;

// MR-14: the webhook is driven through the RequestDelegate that Hosting.MapRecepcionApi really maps on
// POST /webhooks/kapso, with a body signed the way Rules.VerifySignature checks it. No copy of Receive.
public sealed class WhatsAppWebhookTests : IAsyncLifetime
{
    const string Secret = "synthetic-webhook-secret";
    readonly Guid tenant = Guid.NewGuid(); readonly string number = Guid.NewGuid().ToString("N");
    DbContextOptions<CrmDb> options = null!;
    static string Connection => Environment.GetEnvironmentVariable("TEST_DATABASE") ?? throw new InvalidOperationException("Use scripts/test-backend.sh");
    // One built, never started, host for the class: EF caches CrmDb's model, and with it the first data
    // protection provider it saw, so a host disposed after one test would break the next one's encryption.
    static readonly Lazy<(WebApplication App, RequestDelegate Receive)> Host = new(() =>
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = Connection, ["Auth:Authority"] = "https://identity.example.test/realms/hospital", ["KEY_DIRECTORY"] = Directory.CreateTempSubdirectory().FullName, ["KAPSO_WEBHOOK_SECRET"] = Secret, ["PHONE_HASH_KEY"] = "synthetic-phone-key" });
        builder.Services.AddRecepcionServices(builder.Configuration, false);
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
        db.Channels.Add(new Channel { TenantId = tenant, Name = "Synthetic", PhoneNumberId = number, Enabled = true });
        await db.SaveChangesAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask;
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
    [Theory]
    [InlineData("{\"phone_number_id\":")] // truncated
    [InlineData("not json")]
    [InlineData("{\"batch\":true,\"data\":5}")] // valid JSON, not a batch
    [InlineData("{\"batch\":true,\"data\":[1]}")] // a batch item that is not an object
    public async Task SignedMalformedJsonIs400AndWritesNothing(string json)
    {
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
    }
}
