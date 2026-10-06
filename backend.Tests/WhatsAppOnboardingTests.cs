using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
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
    // docs/platform-owner.md AC 10/11: the same cases run for the hospital's Administrador and for the platform owner acting for it.
    void As(string role) { user.Role = role; if (role == "platform") { user.Subject = "platform:owner-sub"; user.Name = "Dueño de plataforma"; } }
    WhatsAppOnboarding Service(Func<HttpRequestMessage,Task<HttpResponseMessage>> callback) => new(db,scope,user,new KapsoClient(new HttpClient(new Fake(callback)),config),config);
    static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    object Number(string? owner=null) => new { phone_number_id=number, customer_id=owner??customer, status="CONNECTED", kind="production", display_phone_number="+503 7000 0000", is_coexistence=true };
    [Theory][InlineData("admin")][InlineData("platform")] public async Task SetupUsesOwnedCustomerSpanishExistingNumbersAndSafeRedirects(string role)
    {
        As(role);
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
    [Theory][InlineData("admin")][InlineData("platform")] public async Task RepeatedSyncRegistersOnceAndDoesNotActivateAgentOrDuplicateWebhook(string role)
    {
        As(role);
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
    [Theory][InlineData("admin")][InlineData("platform")] public async Task ProviderFilterIsNotTrustedForForeignCustomer(string role)
    {
        As(role);
        var result=await Service(_=>Task.FromResult(Json(new{data=new[]{Number(Guid.NewGuid().ToString())},meta=new{total_pages=1}}))).Sync(default);
        Assert.Equal(0,result.Connected);Assert.Empty(await db.Channels.ToListAsync());
    }
    [Theory][InlineData("admin")][InlineData("platform")] public async Task NumberCannotBeReassignedFromAnotherHospital(string role)
    {
        As(role);
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
    // AC 10 (plan §9.2): enabling a number while the reception's agent is on would make the agent answer on it.
    // The platform owner may always pause; it may enable only while the agent is off.
    static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;
    async Task<Channel> Seeded() { var row=new Channel{TenantId=scope.Id,Name="WhatsApp",PhoneNumberId=number,KapsoCustomerId=customer,Enabled=false};db.Channels.Add(row);await db.SaveChangesAsync();return row; }
    [Fact] public async Task PlatformOwnerEnablesANumberOnlyWhileTheAgentIsOffAndCanAlwaysPause()
    {
        var row=await Seeded();As("platform");
        Assert.Equal(200,Status(await WhatsAppEndpoints.SetEnabled(row.Id,new ChannelState(true),db,user,scope)));Assert.True(row.Enabled);
        Assert.Equal(200,Status(await WhatsAppEndpoints.SetEnabled(row.Id,new ChannelState(false),db,user,scope)));Assert.False(row.Enabled);
        (await db.Tenants.SingleAsync(x=>x.Id==scope.Id)).AgentEnabled=true;await db.SaveChangesAsync();
        var audits=await db.Audits.CountAsync();
        var refused=await WhatsAppEndpoints.SetEnabled(row.Id,new ChannelState(true),db,user,scope);
        Assert.Equal(409,Status(refused));Assert.Equal("agent_enabled",Code(refused));
        await db.Entry(row).ReloadAsync();Assert.False(row.Enabled);Assert.Equal(audits,await db.Audits.CountAsync());
        // Pausing stays possible with the agent on: it can only make the agent answer less.
        row.Enabled=true;await db.SaveChangesAsync();
        Assert.Equal(200,Status(await WhatsAppEndpoints.SetEnabled(row.Id,new ChannelState(false),db,user,scope)));Assert.False(row.Enabled);
        Assert.Equal(404,Status(await WhatsAppEndpoints.SetEnabled(Guid.NewGuid(),new ChannelState(false),db,user,scope)));
        Assert.True((await db.Tenants.SingleAsync(x=>x.Id==scope.Id)).AgentEnabled);
    }
    static string? Code(IResult result) => JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value).GetProperty("code").GetString();
    Task<List<string>> Actions() => db.Audits.OrderBy(x=>x.CreatedAt).Select(x=>x.Action).ToListAsync();
    // Channel.Enabled is also the gate of AppointmentReminders: switching on the reminder number would resume automatic sends on behalf.
    [Fact] public async Task PlatformOwnerCannotEnableTheNumberRemindersGoOutOnButCanPauseIt()
    {
        var row=await Seeded();var spare=new Channel{TenantId=scope.Id,Name="Otro",PhoneNumberId=Random.Shared.NextInt64(100000000000000,999999999999999).ToString(),Enabled=false};db.Channels.Add(spare);
        var tenant=await db.Tenants.SingleAsync(x=>x.Id==scope.Id);tenant.RemindersEnabled=true;tenant.ReminderChannelId=row.Id;await db.SaveChangesAsync();
        As("platform");
        var refused=await WhatsAppEndpoints.SetEnabled(row.Id,new ChannelState(true),db,user,scope);
        Assert.Equal(409,Status(refused));Assert.Equal("reminders_enabled",Code(refused));
        await db.Entry(row).ReloadAsync();Assert.False(row.Enabled);Assert.Empty(await Actions());
        // A number reminders do not use carries no automatic sends: it can be enabled. And pausing is always allowed.
        Assert.Equal(200,Status(await WhatsAppEndpoints.SetEnabled(spare.Id,new ChannelState(true),db,user,scope)));Assert.True(spare.Enabled);
        row.Enabled=true;await db.SaveChangesAsync();
        Assert.Equal(200,Status(await WhatsAppEndpoints.SetEnabled(row.Id,new ChannelState(false),db,user,scope)));Assert.False(row.Enabled);
        // On behalf, the trail says which way the switch went.
        Assert.Equal(new[]{"channel.enabled","channel.paused"},await Actions());
        // The agent wins when both are on; the hospital's own Administrador is not restricted and keeps its audit name.
        tenant.AgentEnabled=true;await db.SaveChangesAsync();
        Assert.Equal("agent_enabled",Code(await WhatsAppEndpoints.SetEnabled(row.Id,new ChannelState(true),db,user,scope)));
        As("admin");
        Assert.Equal(200,Status(await WhatsAppEndpoints.SetEnabled(row.Id,new ChannelState(true),db,user,scope)));Assert.True(row.Enabled);
        Assert.Equal(200,Status(await WhatsAppEndpoints.SetEnabled(row.Id,new ChannelState(false),db,user,scope)));
        Assert.Equal(new[]{"channel.enabled","channel.paused","channel.enabled","channel.enabled"},await Actions());
        Assert.True(tenant.RemindersEnabled);Assert.Equal(row.Id,tenant.ReminderChannelId);
    }
    [Fact] public async Task AdministratorStillEnablesWithTheAgentOnAndOtherRolesCannotToggle()
    {
        var row=await Seeded();(await db.Tenants.SingleAsync(x=>x.Id==scope.Id)).AgentEnabled=true;await db.SaveChangesAsync();
        Assert.Equal(200,Status(await WhatsAppEndpoints.SetEnabled(row.Id,new ChannelState(true),db,user,scope)));Assert.True(row.Enabled);
        foreach(var role in new[]{"agent","doctor"}){user.Role=role;await Assert.ThrowsAsync<AccessDeniedException>(()=>WhatsAppEndpoints.SetEnabled(row.Id,new ChannelState(false),db,user,scope));}
    }
    [Fact] public async Task PlatformOwnerCannotPauseOrEnableAnotherReceptionsNumber()
    {
        var other=Guid.NewGuid();db.Tenants.Add(new Tenant{Id=other,Name="Other"});var foreign=new Channel{TenantId=other,Name="Other",PhoneNumberId=number,Enabled=true};db.Channels.Add(foreign);var previous=scope.Id;scope.Id=other;await db.SaveChangesAsync();scope.Id=previous;
        As("platform");
        Assert.Equal(404,Status(await WhatsAppEndpoints.SetEnabled(foreign.Id,new ChannelState(false),db,user,scope)));
        Assert.True(await db.Channels.IgnoreQueryFilters().Where(x=>x.Id==foreign.Id).Select(x=>x.Enabled).SingleAsync());
    }
    [Theory][InlineData("admin")][InlineData("platform")] public async Task DiagnosticsReadTheChannelWithoutReturningTheSigningSecret(string role)
    {
        var row=await Seeded();As(role);
        var kapso=new KapsoClient(new HttpClient(new Fake(req=>{
            var path=req.RequestUri!.AbsolutePath;
            if(path.EndsWith("/health"))return Task.FromResult(Json(new{data=new{status="healthy"}}));
            if(path.EndsWith("/webhooks"))return Task.FromResult(Json(new{data=new[]{new{id="hook",url="https://hooks.example.test/webhooks/kapso",kind="kapso",active=true,secret_key="test-signing-secret",events=WhatsAppOnboarding.Events}}}));
            return Task.FromResult(Json(new{data=Number()}));
        })),config);
        var result=await ChannelDiagnostics.Run(row.Id,db,user,kapso,config);
        Assert.Equal(200,Status(result));
        var wire=JsonSerializer.Serialize(((IValueHttpResult)result).Value);Assert.Contains("\"signatureMatches\":true",wire);Assert.DoesNotContain("test-signing-secret",wire);
    }
    [Theory][InlineData("agent")][InlineData("doctor")] public async Task OtherRolesCannotRunDiagnostics(string role)
    {
        user.Role=role;
        await Assert.ThrowsAsync<AccessDeniedException>(()=>ChannelDiagnostics.Run(Guid.NewGuid(),db,user,new KapsoClient(new HttpClient(new Fake(_=>throw new Exception("Provider must not be called"))),config),config));
    }
    sealed class Fake(Func<HttpRequestMessage,Task<HttpResponseMessage>> callback):HttpMessageHandler {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>callback(request);}
}
