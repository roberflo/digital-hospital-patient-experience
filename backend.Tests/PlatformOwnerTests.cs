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
        "GET /api/platform/tenants", "GET /api/platform/hospitals", "POST /api/platform/tenants",
        "GET /api/hospital/connection", "GET /api/hospital/connection/setup", "PUT /api/hospital/connection", "POST /api/hospital/connection/check",
        "GET /api/platform/installation",
        "GET /api/channels", "POST /api/channels/onboarding", "POST /api/channels/sync", "PATCH /api/channels/{id:guid}", "POST /api/channels/{id:guid}/diagnostics",
    ];
    [Fact] public void MarkedRoutesAreExactlyTheSpecsList()
    {
        Assert.Equal(Operable.Order(StringComparer.Ordinal), Api.Value.Where(x => Mark(x) is not null).Select(Key).Order(StringComparer.Ordinal));
        // Only the list works before a reception is chosen.
        Assert.Equal(new[] { "GET /api/platform/hospitals", "GET /api/platform/tenants", "POST /api/platform/tenants" }, Api.Value.Where(x => Mark(x) is { NeedsTenant: false }).Select(Key).Order(StringComparer.Ordinal));
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

    // ---- REC-3 (AC 17–20): list Hospital's hospitals and open a hospital's reception on its behalf ----
    const string OwnerToken = "owner.token.synthetic";
    static IConfiguration Install(string allowed = "https://hospital-api.example.com") => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HOSPITAL_API_URL"] = "https://hospital-api.example.com", ["HOSPITAL_ALLOWED_API_ORIGINS"] = allowed, ["HOSPITAL_SERVICE_CLIENT_SECRET"] = "synthetic-secret", ["Auth:Authority"] = "https://identity.example.com/realms/hospital" }).Build();
    DefaultHttpContext Bearer() { var ctx = Owner(null); ctx.Request.Headers.Authorization = "Bearer " + OwnerToken; return ctx; }
    CurrentUser Platform() => new() { Role = "platform", Subject = "platform:" + sub, Name = "Dueña Sintética" };
    static object Listed(Guid id, string? name) => new { id, displayName = name, timeZone = "America/El_Salvador", createdAt = "2026-10-06T00:00:00Z" };
    /// <summary>Hospital as the two callers see it: the owner's own token lists hospitals; a hospital's service account reads its clinic.</summary>
    Fake Hospital(object[] hospitals, Func<Guid, bool>? serviceAccountReady = null, List<string>? calls = null) => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath; calls?.Add(path);
        if (path == "/v1/platform/hospitals")
        {
            Assert.Equal("hospital-api.example.com", req.RequestUri.Host); Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme); Assert.Equal(OwnerToken, req.Headers.Authorization?.Parameter);
            return Json(new { hospitals });
        }
        if (path.EndsWith("/protocol/openid-connect/token"))
        {
            Assert.Null(req.Headers.Authorization);
            var form = System.Web.HttpUtility.ParseQueryString(req.Content!.ReadAsStringAsync().Result);
            Assert.Equal("client_credentials", form["grant_type"]); Assert.StartsWith("recepcion-service-", form["client_id"]);
            var id = Guid.Parse(form["client_id"]!["recepcion-service-".Length..]);
            if (serviceAccountReady?.Invoke(id) == false) return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new { error = "invalid_client" }) };
            return Json(new { access_token = "x." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { tenant_id = id, exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".x" });
        }
        Assert.Equal("/v1/clinic", path);
        // The owner's token is never what reads a hospital's own data.
        Assert.NotEqual(OwnerToken, req.Headers.Authorization?.Parameter);
        return Json(new { displayName = "  Hospital Nuevo Sintético  ", timeZone = "America/Mexico_City" });
    });
    Task<IResult> Open(string? hospitalId, Fake hospital, CurrentUser? user = null, IConfiguration? config = null)
    {
        config ??= Install();
        return PlatformOwner.Open(new OpenTenantInput(hospitalId), user ?? Platform(), Bearer(), db, scope, new HospitalClient(new HttpClient(hospital, false), config, new HospitalConnectionStore(db, config)), config, new Factory(hospital), default);
    }
    static string? Code(IResult result) => JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value).GetProperty("code").GetString();
    static int? Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;
    async Task<(int Tenants, int Members, int Audits)> Counts() => (await db.Tenants.CountAsync(), await db.Members.IgnoreQueryFilters().CountAsync(), await db.Audits.IgnoreQueryFilters().CountAsync());

    [Fact] public async Task HospitalsAreListedFromHospitalWithWhetherTheirReceptionIsOpen() // AC 17
    {
        var unnamed = Guid.NewGuid(); var fresh = Guid.NewGuid(); var calls = new List<string>();
        var hospital = Hospital([Listed(fresh, "Clínica Nueva"), Listed(target, "Nombre en Hospital"), Listed(unnamed, null)], calls: calls);
        var result = await PlatformOwner.Hospitals(Platform(), Bearer(), db, Install(), new Factory(hospital), default);
        Assert.Equal(new[] { "/v1/platform/hospitals" }, calls);
        Assert.Equal(new[] { new PlatformHospital(fresh, "Clínica Nueva", false), new PlatformHospital(unnamed, "Hospital sin nombre", false), new PlatformHospital(target, "Nombre en Hospital", true) }, result.Hospitals);
        var wire = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(wire);
        Assert.Equal(new[] { "hospitals" }, doc.RootElement.EnumerateObject().Select(x => x.Name));
        Assert.All(doc.RootElement.GetProperty("hospitals").EnumerateArray(), row => Assert.Equal(new[] { "id", "name", "hasReception" }, row.EnumerateObject().Select(x => x.Name)));
        Assert.DoesNotContain(OwnerToken, wire);
    }
    [Theory][InlineData("admin")][InlineData("agent")][InlineData("doctor")][InlineData("")]
    public async Task OnlyThePlatformOwnerListsHospitalsOrOpensAReception(string role) // AC 17, AC 19
    {
        var hospital = new Fake(_ => throw new Exception("Hospital must not be called"));
        var user = new CurrentUser { Role = role, Subject = "hospital-user" }; var before = await Counts();
        await Assert.ThrowsAsync<AccessDeniedException>(() => PlatformOwner.Hospitals(user, Bearer(), db, Install(), new Factory(hospital), default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Open(Guid.NewGuid().ToString(), hospital, user));
        Assert.Equal(before, await Counts());
    }
    [Theory][InlineData("503")][InlineData("401")][InlineData("network")][InlineData("not-json")][InlineData("wrong-shape")][InlineData("bad-id")]
    public async Task UnreadableHospitalIsABadGatewayNeverAnEmptyList(string failure) // AC 17
    {
        var hospital = new Fake(_ => failure switch
        {
            "503" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "401" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            "network" => throw new HttpRequestException("down"),
            "not-json" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>", Encoding.UTF8, "text/html") },
            "wrong-shape" => Json(new { items = Array.Empty<object>() }),
            _ => Json(new { hospitals = new[] { new { id = "not-a-uuid", displayName = "x" } } }),
        });
        var listing = await Assert.ThrowsAsync<HospitalIntegrationException>(() => PlatformOwner.Hospitals(Platform(), Bearer(), db, Install(), new Factory(hospital), default));
        Assert.Equal(HttpStatusCode.BadGateway, listing.StatusCode); Assert.DoesNotContain(OwnerToken, listing.ToString());
        // Opening needs the same read: it fails the same way and creates nothing.
        var before = await Counts();
        Assert.Equal(HttpStatusCode.BadGateway, (await Assert.ThrowsAsync<HospitalIntegrationException>(() => Open(Guid.NewGuid().ToString(), hospital))).StatusCode);
        Assert.Equal(before, await Counts());
    }
    [Theory][InlineData("https://other-api.example.com")][InlineData("")][InlineData("https://hospital-api.example.com.attacker.example")]
    public async Task OwnersTokenOnlyTravelsToAnAllowedHospitalOrigin(string allowed) // AC 17
    {
        var hospital = new Fake(_ => throw new Exception("The owner's token must not be sent"));
        var error = await Assert.ThrowsAsync<HospitalIntegrationException>(() => PlatformOwner.Hospitals(Platform(), Bearer(), db, Install(allowed), new Factory(hospital), default));
        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
    }
    [Fact] public async Task OpeningCreatesTheReceptionWithHospitalsNameAndZoneAuditedAndNoMember() // AC 18, AC 20
    {
        var id = Guid.NewGuid(); var calls = new List<string>(); var before = await Counts();
        var hospital = Hospital([Listed(target, "Recepción objetivo"), Listed(id, "Nombre del listado")], calls: calls);
        var result = await Open(id.ToString(), hospital);
        Assert.Equal(201, Status(result));
        var body = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(new[] { "id", "name" }, body.EnumerateObject().Select(x => x.Name));
        Assert.Equal(id, body.GetProperty("id").GetGuid()); Assert.Equal("Hospital Nuevo Sintético", body.GetProperty("name").GetString());
        // The same path the connection check uses: the hospital's own service account got a token and read its clinic.
        Assert.Equal(new[] { "/v1/platform/hospitals", "/realms/hospital/protocol/openid-connect/token", "/v1/clinic" }, calls);

        await using var fresh = new CrmDb(new DbContextOptionsBuilder<CrmDb>().UseNpgsql(Environment.GetEnvironmentVariable("TEST_DATABASE")).Options, new TenantScope { Id = id }, protection);
        var row = await fresh.Tenants.SingleAsync(x => x.Id == id);
        Assert.Equal("Hospital Nuevo Sintético", row.Name); Assert.Equal("America/Mexico_City", row.TimeZone);
        // The same initial state a first sign-in leaves: nothing switched on, nothing connected.
        Assert.False(row.AgentEnabled); Assert.False(row.RemindersEnabled); Assert.Null(row.KapsoCustomerId); Assert.Null(row.HospitalConnection); Assert.Equal("", row.Guide);
        Assert.Empty(await fresh.Channels.ToListAsync());
        var after = await Counts();
        Assert.Equal(before.Tenants + 1, after.Tenants); Assert.Equal(before.Members, after.Members); Assert.Equal(before.Audits + 1, after.Audits);
        Assert.Empty(await fresh.Members.ToListAsync());
        Assert.False(await fresh.Members.IgnoreQueryFilters().AnyAsync(x => x.Subject == sub || x.Subject == "platform:" + sub));
        var audit = await fresh.Audits.SingleAsync();
        Assert.Equal("tenant.opened", audit.Action); Assert.Equal(id, audit.TenantId); Assert.Equal("platform:" + sub, audit.Actor); Assert.Equal(id.ToString(), audit.Resource);

        // Opening it again is refused, and the owner can now act for it through the gate.
        var again = await Open(id.ToString(), hospital);
        Assert.Equal(409, Status(again)); Assert.Equal("already_open", Code(again)); Assert.Equal(after, await Counts());
        Assert.Equal(0, await PlatformOwner.Gate(Owner(Api.Value.Single(x => Key(x) == "GET /api/channels"), id), db, new TenantScope(), new CurrentUser()));
    }
    [Fact] public async Task TheHospitalsAdministratorStillJoinsAfterThePlatformOpenedItsReception() // AC 18
    {
        var id = Guid.NewGuid(); var admin = Guid.NewGuid().ToString();
        Assert.Equal(201, Status(await Open(id.ToString(), Hospital([Listed(id, "Clínica")]))));
        var before = await Counts();
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", admin), new Claim("tenant_id", id.ToString()), new Claim("name", "Administradora"), new Claim("iss", "https://identity.example.com/realms/hospital"), new Claim("realm_access", "{\"roles\":[\"Administrador\"]}")], "test")) };
        var current = new CurrentUser();
        Assert.Null(await PlatformOwner.Gate(ctx, db, scope, current));
        Assert.True(await Identity.Bind(ctx, db, scope, current, Install()));
        Assert.Equal("admin", current.Role); Assert.Equal(id, scope.Id); Assert.False(ctx.Items.ContainsKey("tenant-created"));
        var member = await db.Members.SingleAsync(); Assert.Equal(admin, member.Subject); Assert.Equal("admin", member.Role);
        var after = await Counts(); Assert.Equal(before.Tenants, after.Tenants); Assert.Equal(before.Members + 1, after.Members);
        // A second request is the same member, not another one; a receptionist of that hospital joins too.
        Assert.True(await Identity.Bind(ctx, db, scope, new CurrentUser(), Install())); Assert.Equal(after, await Counts());
        Assert.Equal("Hospital Nuevo Sintético", (await db.Tenants.SingleAsync(x => x.Id == id)).Name);
    }
    [Fact] public async Task ServiceAccountNotReadyYetIsAConflictAndCreatesNothing() // AC 19
    {
        var id = Guid.NewGuid(); var before = await Counts();
        var result = await Open(id.ToString(), Hospital([Listed(id, "Clínica")], serviceAccountReady: _ => false));
        Assert.Equal(409, Status(result)); Assert.Equal("service_account_pending", Code(result));
        Assert.Equal(before, await Counts()); Assert.False(await db.Tenants.AnyAsync(x => x.Id == id));
        // Once the reconciler has created the account, the same request opens it.
        Assert.Equal(201, Status(await Open(id.ToString(), Hospital([Listed(id, "Clínica")]))));
    }
    [Fact] public async Task AlreadyOpenReceptionIsAConflictBeforeHospitalIsAsked() // AC 19
    {
        var before = await Counts();
        var result = await Open(target.ToString(), new Fake(_ => throw new Exception("Hospital must not be called")));
        Assert.Equal(409, Status(result)); Assert.Equal("already_open", Code(result)); Assert.Equal(before, await Counts());
    }
    [Fact] public async Task HospitalThatHospitalDoesNotListIsNotFound() // AC 19
    {
        var id = Guid.NewGuid(); var calls = new List<string>(); var before = await Counts();
        Assert.Equal(404, Status(await Open(id.ToString(), Hospital([Listed(target, "Recepción objetivo")], calls: calls))));
        // Not even its service account is tried: an id Hospital does not know never reaches the token endpoint.
        Assert.Equal(new[] { "/v1/platform/hospitals" }, calls);
        Assert.Equal(before, await Counts()); Assert.False(await db.Tenants.AnyAsync(x => x.Id == id));
    }
    [Theory][InlineData(null)][InlineData("")][InlineData("not-a-uuid")][InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task HospitalIdThatIsNotAUuidIsNotFoundWithoutAskingHospital(string? hospitalId) // AC 19
    {
        var before = await Counts();
        Assert.Equal(404, Status(await Open(hospitalId, new Fake(_ => throw new Exception("Hospital must not be called")))));
        Assert.Equal(before, await Counts());
    }
    [Fact] public void TheBodyCarriesOnlyTheHospitalId() // AC 19: no name, zone or anything else from the caller
        => Assert.Equal(new[] { "HospitalId" }, typeof(OpenTenantInput).GetProperties().Select(x => x.Name));
    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request)); }
    sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }
}
