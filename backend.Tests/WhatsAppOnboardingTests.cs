using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Recepcion;
using Recepcion.Integrations;
using Xunit;
[CollectionDefinition("Onboarding database", DisableParallelization = true)]
public sealed class OnboardingDatabaseCollection {}
[Collection("Onboarding database")]
public sealed class WhatsAppOnboardingTests : IAsyncLifetime
{
    CrmDb db = null!; readonly TenantScope scope = new() { Id = Guid.NewGuid() };
    readonly CurrentUser user = new() { Role = "admin", Subject = "test-admin", Name = "Admin" };
    readonly string customer = Guid.NewGuid().ToString();
    readonly string number = Random.Shared.NextInt64(100000000000000, 999999999999999).ToString();
    IConfiguration config = null!;
    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<CrmDb>().UseNpgsql(Environment.GetEnvironmentVariable("TEST_DATABASE") ?? throw new Exception("Use scripts/test-backend.sh")).Options;
        db = new CrmDb(options, scope, new EphemeralDataProtectionProvider()); await db.Database.MigrateAsync();
        db.Tenants.Add(new Tenant { Id = scope.Id, Name = "Test hospital", KapsoCustomerId = customer }); await db.SaveChangesAsync();
        config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["KAPSO_API_KEY"]="synthetic", ["KAPSO_WEBHOOK_URL"]="https://hooks.example.test/webhooks/kapso", ["KAPSO_WEBHOOK_SECRET"]="test-signing-secret", ["FRONTEND_URL"]="https://crm.example.test" }).Build();
    }
    public Task DisposeAsync() => db.DisposeAsync().AsTask();
    WhatsAppOnboarding Service(Func<HttpRequestMessage,Task<HttpResponseMessage>> callback) => new(db,scope,user,new KapsoClient(new HttpClient(new Fake(callback)),config),config);
    static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    object Number(string? owner=null) => new { phone_number_id=number, customer_id=owner??customer, status="CONNECTED", kind="production", display_phone_number="+503 7000 0000", is_coexistence=true };
    [Fact] public async Task SetupUsesOwnedCustomerSpanishExistingNumbersAndSafeRedirects()
    {
        var service=Service(async req=>{
            Assert.EndsWith($"/customers/{customer}/setup_links",req.RequestUri!.AbsolutePath);
            using var doc=JsonDocument.Parse(await req.Content!.ReadAsStringAsync());var options=doc.RootElement.GetProperty("setup_link");
            Assert.Equal("es",options.GetProperty("language").GetString());Assert.False(options.GetProperty("provision_phone_number").GetBoolean());
            Assert.Equal("customer_managed",options.GetProperty("meta_billing_mode").GetString());
            Assert.Contains(options.GetProperty("allowed_connection_types").EnumerateArray(),x=>x.GetString()=="coexistence");
            Assert.Equal("https://crm.example.test/whatsapp?connection=returned",options.GetProperty("success_redirect_url").GetString());
            return Json(new {data=new {url="https://setup.kapso.ai/s/test",expires_at="2026-12-01T00:00:00Z",token="must-not-be-returned"}});
        });
        var result=await service.Start(default);Assert.Equal("https://setup.kapso.ai/s/test",result.Url);Assert.DoesNotContain("must-not",JsonSerializer.Serialize(result));
    }
    [Fact] public async Task NonAdminCannotStartOrSync()
    {
        user.Role="agent";var service=Service(_=>throw new Exception("Provider must not be called"));
        await Assert.ThrowsAsync<AccessDeniedException>(()=>service.Start(default));await Assert.ThrowsAsync<AccessDeniedException>(()=>service.Sync(default));
    }
    [Fact] public async Task RepeatedSyncRegistersOnceAndDoesNotActivateAgentOrDuplicateWebhook()
    {
        var created=false;var writes=0;
        var service=Service(async req=>{
            if(req.RequestUri!.AbsolutePath.EndsWith("/phone_numbers")){Assert.Contains(customer,req.RequestUri.Query);return Json(new{data=new[]{Number()},meta=new{total_pages=1}});}
            if(req.Method==HttpMethod.Get) return Json(new{data=created?new object[]{new{id="hook",url="https://hooks.example.test/webhooks/kapso",kind="kapso",active=true,secret_key="test-signing-secret",payload_version="v2",events=WhatsAppOnboarding.Events}}:Array.Empty<object>()});
            await using var observer = new CrmDb(new DbContextOptionsBuilder<CrmDb>().UseNpgsql(Environment.GetEnvironmentVariable("TEST_DATABASE")).Options,scope,new EphemeralDataProtectionProvider());
            Assert.True(await observer.Channels.AnyAsync(x=>x.PhoneNumberId==number));
            using var doc=JsonDocument.Parse(await req.Content!.ReadAsStringAsync());var hook=doc.RootElement.GetProperty("whatsapp_webhook");
            Assert.Equal("test-signing-secret",hook.GetProperty("secret_key").GetString());Assert.Equal("v2",hook.GetProperty("payload_version").GetString());created=true;writes++;return Json(new{data=new{id="hook"}});
        });
        Assert.Equal(1,(await service.Sync(default)).Added);Assert.Equal(0,(await service.Sync(default)).Added);Assert.Equal(1,writes);
        var row=await db.Channels.SingleAsync();Assert.Equal(customer,row.KapsoCustomerId);Assert.True(row.Coexistence);Assert.False(row.Enabled);Assert.False((await db.Tenants.SingleAsync(x=>x.Id==scope.Id)).AgentEnabled);
    }
    [Fact] public async Task ProviderFilterIsNotTrustedForForeignCustomer()
    {
        var result=await Service(_=>Task.FromResult(Json(new{data=new[]{Number(Guid.NewGuid().ToString())},meta=new{total_pages=1}}))).Sync(default);
        Assert.Equal(0,result.Connected);Assert.Empty(await db.Channels.ToListAsync());
    }
    [Fact] public async Task NumberCannotBeReassignedFromAnotherHospital()
    {
        var other=Guid.NewGuid();db.Tenants.Add(new Tenant{Id=other,Name="Other"});db.Channels.Add(new Channel{TenantId=other,Name="Other",PhoneNumberId=number});var previous=scope.Id;scope.Id=other;await db.SaveChangesAsync();scope.Id=previous;
        await Assert.ThrowsAsync<AccessDeniedException>(()=>Service(_=>Task.FromResult(Json(new{data=new[]{Number()},meta=new{total_pages=1}}))).Sync(default));
    }
    [Fact] public async Task SyncFollowsPaginationAndReportsWebhookFailureWithoutLosingNumber()
    {
        var pages=0;
        var service=Service(req=>{
            if(req.RequestUri!.AbsolutePath.EndsWith("/phone_numbers")){pages++;return Task.FromResult(Json(new{data=pages==1?Array.Empty<object>():new[]{Number()},meta=new{total_pages=2}}));}
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        var result=await service.Sync(default);Assert.Equal(2,pages);Assert.Equal(1,result.Added);Assert.Equal(0,result.WebhooksReady);Assert.Single(result.Warnings);Assert.Single(await db.Channels.ToListAsync());
    }
    [Theory][InlineData("https://evil.test/s/a")][InlineData("http://setup.kapso.ai/s/a")][InlineData("https://setup.kapso.ai.evil.test/s/a")][InlineData("https://user@setup.kapso.ai/s/a")]
    public void SetupUrlRejectsUntrustedDestinations(string url)=>Assert.False(WhatsAppOnboarding.TrustedSetupUrl(url));
    [Theory][InlineData("https://setup.kapso.ai/s/test")][InlineData("https://app.kapso.ai/whatsapp/setup/test")]
    public void SetupUrlAllowsBothOfficialHostedDomains(string url)=>Assert.True(WhatsAppOnboarding.TrustedSetupUrl(url));
    sealed class Fake(Func<HttpRequestMessage,Task<HttpResponseMessage>> callback):HttpMessageHandler {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>callback(request);}
}
