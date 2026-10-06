using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Recepcion;
using Recepcion.Integrations;
using Xunit;

// docs/platform-owner.md: the list of receptions (AC 4), the deny-by-default sweep over every mapped
// /api route (AC 6, 7, 12, 14) and the audit trail of acts done on a reception's behalf (AC 13).
[Collection("Onboarding database")]
public sealed class PlatformOwnerTests : IAsyncLifetime
{
    CrmDb db = null!; readonly TenantScope scope = new(); readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
    readonly Guid target = Guid.NewGuid(), other = Guid.NewGuid(); readonly string sub = Guid.NewGuid().ToString();
    readonly string customer = Guid.NewGuid().ToString();
    readonly string number = Random.Shared.NextInt64(100000000000000, 999999999999999).ToString();
    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<CrmDb>().UseNpgsql(Environment.GetEnvironmentVariable("TEST_DATABASE") ?? throw new Exception("Use scripts/test-backend.sh")).Options;
        db = new CrmDb(options, scope, protection); await db.Database.MigrateAsync();
        db.Tenants.Add(new Tenant { Id = target, Name = "Recepción objetivo", KapsoCustomerId = customer }); db.Tenants.Add(new Tenant { Id = other, Name = "Otra recepción" });
        await db.SaveChangesAsync();
    }
    public Task DisposeAsync() => db.DisposeAsync().AsTask();
    static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    DefaultHttpContext Owner(Endpoint? endpoint, Guid? acting = null)
    {
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", sub), new Claim("name", "Dueña Sintética"), new Claim("realm_access", "{\"roles\":[\"platform-owner\"]}")], "test")) };
        if (acting is { } id) ctx.Request.Headers[PlatformOwner.Header] = id.ToString();
        if (endpoint is not null) ctx.SetEndpoint(endpoint);
        return ctx;
    }

    // ---- AC 4: GET /api/platform/tenants ----
    [Fact] public async Task ListNamesEveryReceptionInOrderWithoutSecrets()
    {
        var tag = "zz-plat-" + Guid.NewGuid().ToString("N");
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).OrderBy(x => x.ToString(), StringComparer.Ordinal).ToArray();
        // Seeded out of order on purpose: b, same (higher id), a, same (lower id).
        db.Tenants.Add(new Tenant { Id = ids[0], Name = tag + "-b" });
        db.Tenants.Add(new Tenant { Id = ids[3], Name = tag + "-same" });
        db.Tenants.Add(new Tenant { Id = ids[1], Name = tag + "-a", KapsoCustomerId = "kapso-customer-must-not-leak-" + tag, Guide = "guía-must-not-leak", HospitalConnection = "{\"ClientSecret\":\"connection-must-not-leak\"}", EmergencyPhone = "5550001234", GoogleRefreshToken = "google-must-not-leak" });
        db.Tenants.Add(new Tenant { Id = ids[2], Name = tag + "-same" });
        await db.SaveChangesAsync();
        // -a has a real number; -b only the synthetic demo channel, which is not a connection.
        scope.Id = ids[1]; db.Channels.Add(new Channel { TenantId = ids[1], Name = "WhatsApp", PhoneNumberId = number, KapsoCustomerId = "kapso-customer-must-not-leak-" + tag }); await db.SaveChangesAsync();
        scope.Id = ids[0]; db.Channels.Add(new Channel { TenantId = ids[0], Name = "Demo", PhoneNumberId = "demo" }); await db.SaveChangesAsync();
        scope.Id = Guid.Empty; // the owner has not chosen a reception when it asks for the list
        var hospital = new HospitalClient(new HttpClient(), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"Hospital:Tenants:{ids[0]}:BaseUrl"] = "https://hospital-must-not-leak.example.test" }).Build());

        var result = await PlatformOwner.Tenants(new CurrentUser { Role = "platform", Subject = "platform:" + sub }, db, hospital);

        var mine = result.Tenants.Where(x => x.Name.StartsWith(tag, StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { ids[1], ids[0], ids[2], ids[3] }, mine.Select(x => x.Id));
        Assert.Equal(new[] { tag + "-a", tag + "-b", tag + "-same", tag + "-same" }, mine.Select(x => x.Name));
        Assert.Equal(new[] { false, true, false, false }, mine.Select(x => x.HospitalConfigured));
        Assert.Equal(new[] { true, false, false, false }, mine.Select(x => x.WhatsAppConnected));
        Assert.Contains(result.Tenants, x => x.Id == target); Assert.Contains(result.Tenants, x => x.Id == other);
        // The wire, as minimal APIs serialize it: exactly the contract's fields, nothing a secret could ride on.
        var wire = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(wire);
        Assert.Equal(new[] { "tenants" }, doc.RootElement.EnumerateObject().Select(x => x.Name));
        Assert.All(doc.RootElement.GetProperty("tenants").EnumerateArray(), row => Assert.Equal(new[] { "id", "name", "hospitalConfigured", "whatsAppConnected" }, row.EnumerateObject().Select(x => x.Name)));
        foreach (var secret in new[] { "must-not-leak", "5550001234", number, "hospitalConnection", "kapsoCustomerId", "guide", "googleRefreshToken" }) Assert.DoesNotContain(secret, wire, StringComparison.OrdinalIgnoreCase);
    }
    [Theory][InlineData("admin")][InlineData("agent")][InlineData("doctor")][InlineData("")]
    public async Task OnlyThePlatformOwnerMayList(string role)
        => await Assert.ThrowsAsync<AccessDeniedException>(() => PlatformOwner.Tenants(new CurrentUser { Role = role, Subject = "hospital-user" }, db, new HospitalClient(new HttpClient(), new ConfigurationBuilder().Build())));

    // ---- AC 6: the sweep. It ENUMERATES the host's real endpoints (Hosting.MapRecepcionApi on a
    // built, never started, WebApplication); it is not a written list of routes. Limit: without a
    // TestHost this asserts the gate's decision per endpoint, not the HTTP status of a live request.
    static readonly Lazy<RouteEndpoint[]> Api = new(() =>
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = "Host=never-opened", ["Auth:Authority"] = "https://identity.example.test/realms/hospital", ["KEY_DIRECTORY"] = Path.GetTempPath() });
        builder.Services.AddRecepcionServices(builder.Configuration, false);
        var app = builder.Build(); app.MapRecepcionApi();
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>().Where(x => x.RoutePattern.RawText == "/api" || x.RoutePattern.RawText!.StartsWith("/api/", StringComparison.Ordinal)).ToArray();
    });
    static string Key(RouteEndpoint e) => string.Join("|", e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["ANY"]) + " " + e.RoutePattern.RawText;
    static PlatformOperable? Mark(Endpoint e) => e.Metadata.GetMetadata<PlatformOperable>();
    // The whole surface the platform owner may operate (spec REC-1 + REC-2, plan §3 and §9.1).
    static readonly string[] Operable =
    [
        "GET /api/platform/tenants",
        "GET /api/hospital/connection", "GET /api/hospital/connection/setup", "PUT /api/hospital/connection", "POST /api/hospital/connection/check",
        "GET /api/platform/installation",
        "GET /api/channels", "POST /api/channels/onboarding", "POST /api/channels/sync", "PATCH /api/channels/{id:guid}", "POST /api/channels/{id:guid}/diagnostics",
    ];
    [Fact] public void MarkedRoutesAreExactlyTheSpecsList()
    {
        Assert.Equal(Operable.Order(StringComparer.Ordinal), Api.Value.Where(x => Mark(x) is not null).Select(Key).Order(StringComparer.Ordinal));
        // Only the list works before a reception is chosen.
        Assert.Equal(new[] { "GET /api/platform/tenants" }, Api.Value.Where(x => Mark(x) is { NeedsTenant: false }).Select(Key));
    }
    [Fact] public async Task EveryOtherMappedApiRouteDeniesThePlatformOwner()
    {
        var keys = Api.Value.Select(Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
        var unmarked = Api.Value.Where(x => Mark(x) is null).ToArray();
        // Not vacuous: the host maps 85 /api routes today, 74 of them closed to the owner; a filter that matched nothing would pass in silence.
        Assert.True(unmarked.Length >= 70, $"only {unmarked.Length} unmarked /api routes were enumerated");
        foreach (var endpoint in unmarked)
        {
            // db is null on purpose: the refusal comes from the missing mark, before any query.
            var user = new CurrentUser(); var acted = new TenantScope();
            var status = await PlatformOwner.Gate(Owner(endpoint, target), null!, acted, user);
            Assert.True(status == 403, $"{Key(endpoint)} answered {status?.ToString() ?? "null (fell through to Bind)"} to the platform owner");
            Assert.Equal(Guid.Empty, acted.Id); Assert.Equal("", user.Role);
        }
    }
    [Theory]
    // AC 7: inbox, conversations, messages, contacts, patients and clinical routes.
    [InlineData("GET /api/conversations")][InlineData("GET /api/conversations/{id:guid}/messages")][InlineData("POST /api/conversations/{id:guid}/messages")]
    [InlineData("POST /api/conversations/{id:guid}/media")][InlineData("GET /api/messages/{id:guid}/media")][InlineData("PATCH /api/conversations/{id:guid}")]
    [InlineData("PATCH /api/conversations/{id:guid}/workflow")][InlineData("POST /api/conversations/{id:guid}/read")][InlineData("POST /api/conversations/assign")]
    [InlineData("GET /api/saved-replies")][InlineData("GET /api/inbox-views")][InlineData("GET /api/macros")]
    [InlineData("GET /api/contacts")][InlineData("POST /api/contacts")][InlineData("PUT /api/contacts/{id:guid}")][InlineData("GET /api/contacts/{id:guid}/context")]
    [InlineData("POST /api/contacts/{id:guid}/patient")]
    [InlineData("GET /api/hospital/conversations/{id:guid}/clinical/{section}")][InlineData("GET /api/hospital/conversations/{id:guid}/clinical/{section}/{documentId:guid}")]
    [InlineData("GET /api/hospital/agenda")][InlineData("GET /api/hospital/availability")][InlineData("POST /api/hospital/appointments")]
    [InlineData("GET /api/hospital/contacts/{id:guid}/appointments")][InlineData("GET /api/hospital/contacts/{id:guid}/prescriptions")]
    // AC 12: the connection is operable, the patient search is not.
    [InlineData("POST /api/hospital/contacts/{id:guid}/patients/search")]
    // AC 14: the agent is switched on in settings; the owner reaches neither settings nor the agent.
    [InlineData("GET /api/settings")][InlineData("PUT /api/settings")][InlineData("POST /api/assistant")][InlineData("GET /api/agent-metrics")][InlineData("GET /api/jobs")]
    // Anti-criterion: a number supplied by the caller.
    [InlineData("POST /api/channels")]
    [InlineData("GET /api/me")][InlineData("GET /api/overview")][InlineData("GET /api/members")][InlineData("GET /api/audit")]
    public async Task NamedRoutesAreMappedAndDenyThePlatformOwner(string route)
    {
        var endpoint = Assert.Single(Api.Value, x => Key(x) == route);
        Assert.Null(Mark(endpoint));
        Assert.Equal(403, await PlatformOwner.Gate(Owner(endpoint, target), null!, new TenantScope(), new CurrentUser()));
    }
    [Fact] public async Task MarkedRoutesAdmitTheOwnerForTheChosenReceptionOnly()
    {
        foreach (var endpoint in Api.Value.Where(x => Mark(x) is { NeedsTenant: true }))
        {
            var acted = new TenantScope(); var user = new CurrentUser();
            Assert.Equal(0, await PlatformOwner.Gate(Owner(endpoint, target), db, acted, user));
            Assert.Equal(target, acted.Id); Assert.Equal("platform:" + sub, user.Subject);
            Assert.Equal(403, await PlatformOwner.Gate(Owner(endpoint), db, new TenantScope(), new CurrentUser()));
            Assert.Equal(404, await PlatformOwner.Gate(Owner(endpoint, Guid.NewGuid()), db, new TenantScope(), new CurrentUser()));
        }
    }

    // ---- AC 13: every act on a reception's behalf is audited as platform:<sub> on the target reception ----
    [Fact] public async Task ActsOnBehalfAreAuditedOnTheTargetReceptionAsThePlatformActor()
    {
        // The actor and the tenant come out of the gate, exactly as the middleware hands them to the handlers.
        var user = new CurrentUser();
        Assert.Equal(0, await PlatformOwner.Gate(Owner(Api.Value.Single(x => Key(x) == "POST /api/channels/sync"), target), db, scope, user));
        var kapsoConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["KAPSO_API_KEY"] = "synthetic", ["KAPSO_WEBHOOK_URL"] = "https://hooks.example.test/webhooks/kapso", ["KAPSO_WEBHOOK_SECRET"] = "test-signing-secret" }).Build();
        var kapso = new KapsoClient(new HttpClient(new Fake(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/setup_links")) return Json(new { data = new { url = "https://setup.kapso.ai/s/test" } });
            if (path.EndsWith("/health")) return Json(new { data = new { status = "healthy" } });
            if (path.EndsWith("/" + number)) return Json(new { data = new { phone_number_id = number, kind = "production" } });
            if (path.EndsWith("/phone_numbers")) return Json(new { data = new[] { new { phone_number_id = number, customer_id = customer, status = "CONNECTED", kind = "production", display_phone_number = "+503 7000 0000" } }, meta = new { total_pages = 1 } });
            return req.Method == HttpMethod.Get ? Json(new { data = Array.Empty<object>() }) : Json(new { data = new { id = "hook" } });
        })), kapsoConfig);
        var onboarding = new WhatsAppOnboarding(db, scope, user, kapso, kapsoConfig);
        await onboarding.Start(default);
        Assert.Equal(1, (await onboarding.Sync(default)).Added);
        var channel = await db.Channels.SingleAsync();
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await WhatsAppEndpoints.SetEnabled(channel.Id, new ChannelState(false), db, user, scope)).StatusCode);
        // A sync that adds nothing still rewrites the channel and touches the provider's webhook: it leaves its own row.
        Assert.Equal(0, (await onboarding.Sync(default)).Added);
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await ChannelDiagnostics.Run(channel.Id, db, user, kapso, kapsoConfig)).StatusCode);

        // Hospital answers through the reception's own service account; the owner's token is never forwarded.
        var serviceToken = "x." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { tenant_id = target, exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".x";
        var hospitalFake = new Fake(req =>
        {
            Assert.DoesNotContain(sub, req.Headers.Authorization?.Parameter ?? "");
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/protocol/openid-connect/token")) return Json(new { access_token = serviceToken });
            if (path == "/v1/clinic") return Json(new { displayName = "Recepción objetivo", timeZone = "America/El_Salvador" });
            Assert.Equal("/v1/agenda/booking-options", path);
            return Json(new { clinicalDayFrom = "2026-10-06", clinicalDayTo = "2026-10-06", maxDaysPerQuery = 31, rollState = "open", professionals = Array.Empty<object>() });
        });
        var hospital = new HospitalClient(new HttpClient(hospitalFake, false), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"Hospital:Tenants:{target}:BaseUrl"] = "https://hospital-api.example.com", [$"Hospital:Tenants:{target}:AccessToken"] = serviceToken }).Build());
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await HospitalConnectionEndpoints.Check(db, scope, user, hospital, NullLogger<HospitalClient>.Instance, default)).StatusCode);
        var connectConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HOSPITAL_ALLOWED_API_ORIGINS"] = "https://hospital-api.example.com", ["Auth:Authority"] = "https://identity.example.com/realms/hospital" }).Build();
        var saved = await HospitalConnectionEndpoints.Save(new("https://hospital-api.example.com", "https://hospital.example.com", $"recepcion-service-{target:D}", "synthetic-secret"), user, scope, db, connectConfig, new Factory(hospitalFake), new ConversationService(db, scope, kapso), default);
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(saved).StatusCode);

        await using var fresh = new CrmDb(new DbContextOptionsBuilder<CrmDb>().UseNpgsql(Environment.GetEnvironmentVariable("TEST_DATABASE")).Options, new TenantScope { Id = target }, protection);
        var audits = await fresh.Audits.IgnoreQueryFilters().Where(x => x.Actor.EndsWith(sub)).ToListAsync();
        Assert.All(audits, x => { Assert.Equal(target, x.TenantId); Assert.Equal("platform:" + sub, x.Actor); });
        // Every act by name, as many times as it was done: start, two syncs (one adding the number), pause, diagnostics, check, save.
        Assert.Equal(new[] { "channel.connected", "channel.diagnosed", "channel.onboarding", "channel.paused", "channel.synced", "channel.synced", "hospital.checked", "hospital.connected" }, audits.Select(x => x.Action).Order(StringComparer.Ordinal));
        // INV-R1: none of it made the owner a Member, here or anywhere, and nothing landed on the other reception.
        Assert.False(await fresh.Members.IgnoreQueryFilters().AnyAsync(x => x.Subject == sub || x.Subject == "platform:" + sub));
        Assert.False(await fresh.Audits.IgnoreQueryFilters().AnyAsync(x => x.TenantId == other));
        Assert.False(await fresh.Channels.IgnoreQueryFilters().AnyAsync(x => x.TenantId == other));
        Assert.False((await fresh.Tenants.SingleAsync(x => x.Id == target)).AgentEnabled);
    }
    // The connection read tells the view whether the reception's agent is on, so it does not offer «Habilitar»
    // for a number the owner would be refused (409). Same shape for the Administrador and for the platform owner.
    [Theory][InlineData("admin")][InlineData("platform")]
    public async Task ConnectionReadCarriesTheReceptionsAgentState(string role)
    {
        scope.Id = target;
        var user = new CurrentUser { Role = role, Subject = role == "platform" ? "platform:" + sub : "hospital-admin", Name = "Sintética" };
        var hospital = new HospitalClient(new HttpClient(), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"Hospital:Tenants:{target}:BaseUrl"] = "https://hospital-api.example.com" }).Build());
        async Task<JsonElement> Read() => JsonSerializer.SerializeToElement(await HospitalConnectionEndpoints.Read(db, scope, user, hospital, Owner(null), new ConfigurationBuilder().Build()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var off = await Read();
        Assert.Equal(target, off.GetProperty("hospital").GetProperty("id").GetGuid()); Assert.Equal("Recepción objetivo", off.GetProperty("hospital").GetProperty("name").GetString());
        Assert.True(off.GetProperty("configured").GetBoolean()); Assert.Equal(role, off.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.False, off.GetProperty("hospital").GetProperty("agentEnabled").ValueKind);
        Assert.Equal(JsonValueKind.False, off.GetProperty("hospital").GetProperty("remindersEnabled").ValueKind);
        (await db.Tenants.SingleAsync(x => x.Id == target)).AgentEnabled = true; await db.SaveChangesAsync();
        var agentOn = (await Read()).GetProperty("hospital");
        Assert.Equal(JsonValueKind.True, agentOn.GetProperty("agentEnabled").ValueKind); Assert.Equal(JsonValueKind.False, agentOn.GetProperty("remindersEnabled").ValueKind);
        var tenant = await db.Tenants.SingleAsync(x => x.Id == target); tenant.AgentEnabled = false; tenant.RemindersEnabled = true; await db.SaveChangesAsync();
        var remindersOn = (await Read()).GetProperty("hospital");
        Assert.Equal(JsonValueKind.False, remindersOn.GetProperty("agentEnabled").ValueKind); Assert.Equal(JsonValueKind.True, remindersOn.GetProperty("remindersEnabled").ValueKind);
        // Still only what it carried before plus the flag: no guide, no connection values, no Kapso customer.
        Assert.Equal(new[] { "id", "name", "timeZone", "agentEnabled", "remindersEnabled" }, off.GetProperty("hospital").EnumerateObject().Select(x => x.Name));
    }
    [Fact] public async Task ConnectionCheckByTheHospitalsOwnAdministratorStaysUnaudited()
    {
        scope.Id = target;
        var token = "x." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { tenant_id = target, exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".x";
        var hospital = new HospitalClient(new HttpClient(new Fake(req => req.RequestUri!.AbsolutePath == "/v1/clinic" ? Json(new { displayName = "Recepción objetivo", timeZone = "America/El_Salvador" }) : Json(new { clinicalDayFrom = "2026-10-06", clinicalDayTo = "2026-10-06", maxDaysPerQuery = 31, rollState = "open", professionals = Array.Empty<object>() }))), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"Hospital:Tenants:{target}:BaseUrl"] = "https://hospital-api.example.com", [$"Hospital:Tenants:{target}:AccessToken"] = token }).Build());
        await HospitalConnectionEndpoints.Check(db, scope, new CurrentUser { Role = "admin", Subject = "hospital-admin" }, hospital, NullLogger<HospitalClient>.Instance, default);
        Assert.False(await db.Audits.AnyAsync(x => x.Action == "hospital.checked"));
    }
    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request)); }
    sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }
}
