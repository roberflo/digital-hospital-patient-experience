using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Recepcion;
using Recepcion.Integrations;
using Xunit;
public sealed class SecurityTests {
    [Theory][InlineData("")][InlineData("garbage")][InlineData("00")][InlineData("sha256=abc")]
    public void InvalidSignaturesFailClosed(string signature)=>Assert.False(Rules.VerifySignature(Encoding.UTF8.GetBytes("{}"),signature,"secret"));
    [Fact]public void SignatureCoversExactBody(){var body=Encoding.UTF8.GetBytes("{\"text\":\"á\"}");var sig=Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"),body));Assert.True(Rules.VerifySignature(body,sig,"secret"));Assert.False(Rules.VerifySignature(Encoding.UTF8.GetBytes("{}"),sig,"secret"));Assert.False(Rules.VerifySignature(body,sig,"different"));}
    [Fact]public void EmptySecretNeverAccepted()=>Assert.False(Rules.VerifySignature([],Convert.ToHexString(HMACSHA256.HashData([],[])),""));
    [Theory][InlineData("50370000000","50370000000")][InlineData("+503 7000-0000","50370000000")]
    public void PhoneCanonicalization(string input,string output)=>Assert.Equal(output,Rules.Phone(input));
    [Theory][InlineData("123")][InlineData("not a number")][InlineData("1234567890123456")]
    public void BadPhoneRejected(string phone)=>Assert.Throws<ArgumentException>(()=>Rules.Phone(phone));
    [Fact]public void HashUsesSecretAndNormalizedPhone(){Assert.Equal(Rules.PhoneHash("+503 7000-0000","secret"),Rules.PhoneHash("50370000000","secret"));Assert.NotEqual(Rules.PhoneHash("50370000000","secret"),Rules.PhoneHash("50370000000","other"));}
    [Fact]public void WhatsAppWindowBoundary(){var now=DateTimeOffset.UtcNow;Assert.False(Rules.WithinWindow(null,now));Assert.False(Rules.WithinWindow(now.AddHours(-24),now));Assert.False(Rules.WithinWindow(now.AddMinutes(1),now));Assert.True(Rules.WithinWindow(now.AddHours(-23),now));}
    [Theory][InlineData("[]")][InlineData("{\"roles\":42}")][InlineData("invalid")]
    public void MalformedRolesDenied(string json){var p=new ClaimsPrincipal(new ClaimsIdentity([new Claim("realm_access",json)],"test"));Assert.Null(Identity.MapRole(p));}
    // Hospital's seven PRD §3 roles are the whole vocabulary: nothing Recepción once invented, no
    // service capability and no self-asserted `role` claim opens a workspace.
    [Theory][InlineData("Administrador","admin")][InlineData("Médicos","doctor")][InlineData("Odontólogos","doctor")][InlineData("Nutricionistas","doctor")][InlineData("Recepción","agent")][InlineData("Admisión","agent")]
    [InlineData("Enfermería",null)][InlineData("reception-agent",null)][InlineData("platform_admin",null)][InlineData("supervisor",null)][InlineData("Supervisor",null)][InlineData("admin",null)][InlineData("agent",null)][InlineData("doctor",null)]
    public void OnlyHospitalRolesMap(string hospitalRole,string? expected){var p=new ClaimsPrincipal(new ClaimsIdentity([new Claim("realm_access","{\"roles\":[\""+hospitalRole+"\"]}"),new Claim("role","admin")],"test"));Assert.Equal(expected,Identity.MapRole(p));}
    [Theory][InlineData("Recepción","reception-agent",null,null)][InlineData("Administrador","reception-agent",null,null)][InlineData("Recepción",null,"service-account-x",null)][InlineData("Recepción",null,"ana","agent")]
    public void ServiceAccountsNeverMapToARole(string role,string? extraRole,string? username,string? expected)
    {
        var roles=string.Join(",",new[]{role,extraRole}.Where(x=>x is not null).Select(x=>"\""+x+"\""));
        var claims=new List<Claim>{new("realm_access","{\"roles\":["+roles+"]}")}; if(username is not null)claims.Add(new("preferred_username",username));
        Assert.Equal(expected,Identity.MapRole(new ClaimsPrincipal(new ClaimsIdentity(claims,"test"))));
    }
    // INV-R3 (docs/platform-owner.md): the platform owner is never a teammate, whatever else the token carries.
    [Theory][InlineData("\"platform-owner\"")][InlineData("\"platform-owner\",\"Administrador\"")][InlineData("\"Administrador\",\"platform-owner\"")][InlineData("\"Médicos\",\"platform-owner\"")][InlineData("\"platform-owner\",\"Recepción\"")]
    public void PlatformOwnerNeverMapsToAHospitalRole(string roles){var p=new ClaimsPrincipal(new ClaimsIdentity([new Claim("realm_access","{\"roles\":["+roles+"]}"),new Claim("role","admin")],"test"));Assert.True(PlatformOwner.Is(p));Assert.Null(Identity.MapRole(p));}
    [Fact]public void PlatformActorIsNotAnAdministrator()
    {
        var platform=new CurrentUser{Role="platform"};Assert.True(platform.Platform);Assert.False(platform.Admin);Assert.Throws<AccessDeniedException>(platform.RequireAdmin);platform.RequireAdminOrPlatform();
        new CurrentUser{Role="admin"}.RequireAdminOrPlatform();
        foreach(var role in new[]{"agent","doctor","","platform-owner"})Assert.Throws<AccessDeniedException>(new CurrentUser{Role=role}.RequireAdminOrPlatform);
        Assert.False(PlatformOwner.Is(new ClaimsPrincipal(new ClaimsIdentity([new Claim("realm_access","{\"roles\":[\"Administrador\"]}"),new Claim("role","platform-owner")],"test"))));
    }
    [Fact]public void ClinicalRoleMapsButDoesNotBecomeAdmin(){var p=new ClaimsPrincipal(new ClaimsIdentity([new Claim("realm_access","{\"roles\":[\"Médicos\"]}")],"test"));Assert.Equal("doctor",Identity.MapRole(p));Assert.False(new CurrentUser{Role="doctor"}.Admin);}
    [Fact]public void CalendarIdsAreStableAndTenantBound(){var a=Guid.NewGuid();var b=Guid.NewGuid();var appointment=Guid.NewGuid();Assert.Equal(GoogleCalendarClient.EventId(a,appointment),GoogleCalendarClient.EventId(a,appointment));Assert.NotEqual(GoogleCalendarClient.EventId(a,appointment),GoogleCalendarClient.EventId(b,appointment));Assert.Matches("^[0-9a-f]{64}$",GoogleCalendarClient.EventId(a,appointment));}
    // MR-8, MR-9: the error handler Program.cs installs turns a Hospital timeout into 504 and logs every
    // Hospital integration failure by code and trace only: no body, URL, token or identifier.
    [Fact] public async Task HospitalTimeoutIs504AndIntegrationFailuresLogCodeAndTraceOnly()
    {
        var patient=Guid.NewGuid();var logs=new CapturedLogs();
        var services=new ServiceCollection().AddLogging(b=>b.AddProvider(logs)).BuildServiceProvider();
        async Task<(int Status,string Title,string Trace)> Run(Exception thrown)
        {
            var ctx=new DefaultHttpContext{RequestServices=services,TraceIdentifier="trace-"+Guid.NewGuid().ToString("N")};
            ctx.Request.Path=$"/api/contacts/{patient}/appointments";ctx.Request.QueryString=new($"?patientId={patient}");ctx.Response.Body=new MemoryStream();
            await Hosting.Errors(ctx,_=>throw thrown);
            ctx.Response.Body.Position=0;using var problem=System.Text.Json.JsonDocument.Parse(ctx.Response.Body);
            return (ctx.Response.StatusCode,problem.RootElement.GetProperty("title").GetString()!,ctx.TraceIdentifier);
        }
        var timeout=await Run(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",new TimeoutException()));
        Assert.Equal(504,timeout.Status);Assert.Equal("Hospital no respondió a tiempo. Comprueba la agenda antes de reintentar.",timeout.Title);
        var refused=await Run(new HospitalIntegrationException("hospital.http_409",System.Net.HttpStatusCode.Conflict));
        Assert.Equal(409,refused.Status);
        Assert.Contains(logs.Lines,line=>line.Contains("hospital.http_409")&&line.Contains(refused.Trace));
        Assert.DoesNotContain(logs.Lines,line=>line.Contains(patient.ToString())||line.Contains("/api/"));
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"backend","Program.cs")))root=root.Parent;
        Assert.Contains("app.Use(Hosting.Errors);",File.ReadAllText(Path.Combine(root!.FullName,"backend","Program.cs")));
    }
    sealed class CapturedLogs:Microsoft.Extensions.Logging.ILoggerProvider,Microsoft.Extensions.Logging.ILogger
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Lines=new();
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string category)=>this;
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level)=>true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level,Microsoft.Extensions.Logging.EventId id,TState state,Exception? error,Func<TState,Exception?,string> format)=>Lines.Enqueue(format(state,error)+" "+error);
        public void Dispose(){}
    }
}

// docs/platform-owner.md AC 2 and AC 5: the gate that runs before Identity.Bind, row by row of the
// plan's response table, against two receptions. No case may create a Tenant or a Member.
[Collection("Onboarding database")]
public sealed class PlatformGateTests : IAsyncLifetime
{
    CrmDb db = null!; readonly Guid a = Guid.NewGuid(), b = Guid.NewGuid(); readonly string sub = Guid.NewGuid().ToString();
    int tenants, members;
    static readonly Endpoint Acting = new(null, new EndpointMetadataCollection(new PlatformOperable()), "acting");
    static readonly Endpoint Listing = new(null, new EndpointMetadataCollection(new PlatformOperable(NeedsTenant: false)), "list");
    static readonly Endpoint Unmarked = new(null, EndpointMetadataCollection.Empty, "unmarked");
    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<CrmDb>().UseNpgsql(Environment.GetEnvironmentVariable("TEST_DATABASE") ?? throw new Exception("Use scripts/test-backend.sh")).Options;
        db = new CrmDb(options, new TenantScope(), new EphemeralDataProtectionProvider()); await db.Database.MigrateAsync();
        db.Tenants.Add(new Tenant { Id = a, Name = "Recepción A" }); db.Tenants.Add(new Tenant { Id = b, Name = "Recepción B" }); await db.SaveChangesAsync();
        tenants = await db.Tenants.CountAsync(); members = await db.Members.IgnoreQueryFilters().CountAsync();
    }
    public Task DisposeAsync() => db.DisposeAsync().AsTask();
    DefaultHttpContext Request(string roles, Endpoint? endpoint, Guid? tenantClaim = null, string? name = "Dueña Sintética", params string[] acting)
    {
        var claims = new List<Claim> { new("sub", sub), new("realm_access", "{\"roles\":[" + roles + "]}"), new("iss", "https://identity.example.test/realms/hospital") };
        if (tenantClaim is { } id) claims.Add(new("tenant_id", id.ToString())); if (name is not null) claims.Add(new("name", name));
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        if (acting.Length > 0) ctx.Request.Headers[PlatformOwner.Header] = acting;
        if (endpoint is not null) ctx.SetEndpoint(endpoint);
        return ctx;
    }
    async Task<(int? Status, TenantScope Scope, CurrentUser User)> Gate(DefaultHttpContext ctx)
    {
        var scope = new TenantScope(); var user = new CurrentUser();
        var status = await PlatformOwner.Gate(ctx, db, scope, user);
        // INV-R1 and the anti-criteria: choosing never creates the space, the owner is never a Member.
        Assert.Equal(tenants, await db.Tenants.CountAsync()); Assert.Equal(members, await db.Members.IgnoreQueryFilters().CountAsync());
        Assert.False(await db.Members.IgnoreQueryFilters().AnyAsync(x => x.Subject == sub || x.Subject == "platform:" + sub));
        if (status != 0) { Assert.Equal(Guid.Empty, scope.Id); Assert.Equal("", user.Role); Assert.Equal("", user.Subject); }
        return (status, scope, user);
    }
    const string Owner = "\"platform-owner\"";
    [Fact] public async Task OwnerTokenThatAlsoCarriesATenantIsRejected() // INV-R5
        => Assert.Equal(401, (await Gate(Request(Owner, Acting, tenantClaim: a, acting: b.ToString()))).Status);
    [Theory][InlineData("\"Administrador\"")][InlineData("\"Recepción\"")][InlineData("\"Médicos\"")]
    public async Task HospitalTokenWithTheHeaderIsRejectedNotIgnored(string roles) // INV-R5
    {
        Assert.Equal(403, (await Gate(Request(roles, Acting, tenantClaim: a, acting: b.ToString()))).Status);
        Assert.Equal(403, (await Gate(Request(roles, Acting, tenantClaim: a, acting: a.ToString()))).Status);
        Assert.Equal(403, (await Gate(Request(roles, Unmarked, tenantClaim: a, acting: ""))).Status);
    }
    [Fact] public async Task HospitalTokenWithoutTheHeaderIsLeftToBind()
        => Assert.Null((await Gate(Request("\"Administrador\"", Acting, tenantClaim: a))).Status);
    [Fact] public async Task OwnerIsDeniedOnUnmarkedRoutesAndWhenNoRouteMatched() // INV-R2
    {
        Assert.Equal(403, (await Gate(Request(Owner, Unmarked, acting: a.ToString()))).Status);
        Assert.Equal(403, (await Gate(Request(Owner, Unmarked))).Status);
        Assert.Equal(403, (await Gate(Request(Owner, null, acting: a.ToString()))).Status);
        Assert.Equal(403, (await Gate(Request(Owner + ",\"Administrador\"", Unmarked, acting: a.ToString()))).Status);
    }
    [Fact] public async Task OwnerWithoutAChosenReceptionOnlyReachesTheList() // AC 2
    {
        Assert.Equal(403, (await Gate(Request(Owner, Acting))).Status);
        var (status, scope, user) = await Gate(Request(Owner, Listing));
        Assert.Equal(0, status); Assert.Equal(Guid.Empty, scope.Id);
        Assert.Equal("platform", user.Role); Assert.Equal("platform:" + sub, user.Subject); Assert.False(user.Admin);
        // The list is the same whatever was chosen before: a stale choice never locks the owner out of choosing again.
        var chosen = await Gate(Request(Owner, Listing, acting: Guid.NewGuid().ToString()));
        Assert.Equal(0, chosen.Status); Assert.Equal(Guid.Empty, chosen.Scope.Id);
        // And Bind, were it ever reached with this token, admits nobody and writes nothing.
        Assert.False(await Identity.Bind(Request(Owner, Listing), db, new TenantScope(), new CurrentUser(), new ConfigurationBuilder().Build()));
        Assert.Equal(tenants, await db.Tenants.CountAsync()); Assert.Equal(members, await db.Members.IgnoreQueryFilters().CountAsync());
    }
    [Fact] public async Task ChosenReceptionMustExistAndBeExactlyOneUuid() // INV-R4
    {
        var absent = Guid.NewGuid();
        foreach (var acting in new string[][] { [absent.ToString()], [""], ["not-a-uuid"], [Guid.Empty.ToString()], [a.ToString(), b.ToString()], [a.ToString(), a.ToString()], [a + "," + b], [a.ToString("N")], ["{" + a + "}"] })
            Assert.Equal(404, (await Gate(Request(Owner, Acting, acting: acting))).Status);
        Assert.False(await db.Tenants.AnyAsync(x => x.Id == absent));
    }
    [Fact] public async Task ChosenReceptionBecomesTheTenantOfTheAct() // AC 5
    {
        foreach (var chosen in new[] { a, b })
        {
            var (status, scope, user) = await Gate(Request(Owner, Acting, acting: chosen.ToString()));
            Assert.Equal(0, status); Assert.Equal(chosen, scope.Id);
            Assert.Equal("platform", user.Role); Assert.Equal("platform:" + sub, user.Subject); Assert.Equal("Dueña Sintética", user.Name);
            Assert.True(user.Platform); Assert.False(user.Admin);
        }
        Assert.Equal("Dueño de plataforma", (await Gate(Request(Owner, Acting, name: null, acting: a.ToString()))).User.Name);
        // Holding Administrador as well changes nothing: still the platform actor, never admin (INV-R3).
        var both = await Gate(Request(Owner + ",\"Administrador\"", Acting, acting: b.ToString()));
        Assert.Equal(0, both.Status); Assert.Equal("platform", both.User.Role); Assert.Equal(b, both.Scope.Id);
    }
}
