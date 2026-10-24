using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Recepcion;
using Recepcion.Integrations;
using Xunit;
public sealed class SecurityTests {
    [Theory][InlineData("")][InlineData("garbage")][InlineData("00")][InlineData("sha256=abc")]
    public void InvalidSignaturesFailClosed(string signature)=>Assert.False(Rules.VerifySignature(Encoding.UTF8.GetBytes("{}"),signature,"secret"));
    [Fact]public void SignatureCoversExactBody(){var body=Encoding.UTF8.GetBytes("{\"text\":\"á\"}");var sig=Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"),body));Assert.True(Rules.VerifySignature(body,sig,"secret"));Assert.False(Rules.VerifySignature(Encoding.UTF8.GetBytes("{}"),sig,"secret"));Assert.False(Rules.VerifySignature(body,sig,"different"));}
    [Fact]public void EmptySecretNeverAccepted()=>Assert.False(Rules.VerifySignature([],Convert.ToHexString(HMACSHA256.HashData([],[])),""));
    [Theory][InlineData("50370000000","50370000000")][InlineData("+503 7000-0000","50370000000")][InlineData("+683 4001","6834001")][InlineData("6834001","6834001")]
    public void PhoneCanonicalization(string input,string output)=>Assert.Equal(output,Rules.Phone(input));
    [Theory][InlineData("7000-0000","50370000000")][InlineData("2200 0000","50322000000")][InlineData("50370000000","50370000000")][InlineData("+503 7000-0000","50370000000")]
    [InlineData("+50012345","50012345")][InlineData("51234567","51234567")][InlineData("14155550100","14155550100")][InlineData("+52 55 1234 5678","525512345678")][InlineData("+683 4001","6834001")]
    public void ContactPhoneAssumesElSalvadorOnlyForLocalNumbers(string input,string output)=>Assert.Equal(output,Rules.ContactPhone(input));
    [Theory][InlineData("123")][InlineData("not a number")][InlineData("1234567890123456")][InlineData("123456")]
    public void BadPhoneRejected(string phone)=>Assert.Throws<ArgumentException>(()=>Rules.Phone(phone));
    /// <summary>INV-7: seven digits typed by a person without «+» are most likely a Salvadoran number missing a digit.</summary>
    [Theory][InlineData("7000000")][InlineData("700-0000")]
    public void ContactPhoneRejectsSevenLocalDigits(string phone)=>Assert.Throws<ArgumentException>(()=>Rules.ContactPhone(phone));
    [Theory][InlineData("+683 4001","6834001")][InlineData("50370000000","50370000000")][InlineData("123456",null)][InlineData("0123456",null)]
    public void HospitalPhonesMatchFromSevenDigits(string phone,string? digits)=>Assert.Equal(digits,Recepcion.Integrations.HospitalClient.NormalizePhone(phone));
    [Fact]public void HashUsesSecretAndNormalizedPhone(){Assert.Equal(Rules.PhoneHash("+503 7000-0000","secret"),Rules.PhoneHash("50370000000","secret"));Assert.NotEqual(Rules.PhoneHash("50370000000","secret"),Rules.PhoneHash("50370000000","other"));
        // Criterio 20, INV-4: the hash domain is pinned. Computed before 7-digit numbers were allowed; a change here orphans every BAJA.
        Assert.Equal(Rules.PhoneHash("+50370000000","secret"),Rules.PhoneHash("50370000000","secret"));Assert.Equal("27E626995EAA1BC0B82B6F2BB5D743226E17B7FB23ABA62E7D7C7C4DC944400A",Rules.PhoneHash("50370000000","secret"));}
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
        Task<(int Status,string Title,string Trace)> Run(Exception thrown)=>Call(_=>throw thrown);
        async Task<(int Status,string Title,string Trace)> Call(RequestDelegate next)
        {
            var ctx=new DefaultHttpContext{RequestServices=services,TraceIdentifier="trace-"+Guid.NewGuid().ToString("N")};
            ctx.Request.Path=$"/api/contacts/{patient}/appointments";ctx.Request.QueryString=new($"?patientId={patient}");ctx.Response.Body=new MemoryStream();
            await Hosting.Errors(ctx,next);
            ctx.Response.Body.Position=0;using var problem=System.Text.Json.JsonDocument.Parse(ctx.Response.Body);
            return (ctx.Response.StatusCode,problem.RootElement.GetProperty("title").GetString()!,ctx.TraceIdentifier);
        }
        // Hospital's HttpClient timing out, through the real HospitalClient, is the only «Hospital no respondió».
        var tenant=Guid.NewGuid();
        var token="e30."+Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new{tenant_id=tenant,exp=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()})).TrimEnd('=').Replace('+','-').Replace('/','_')+".synthetic";
        var hospital=new HospitalClient(new HttpClient(new TimingOut()),new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{[$"Hospital:Tenants:{tenant}:BaseUrl"]="https://hospital.example.invalid/",[$"Hospital:Tenants:{tenant}:AccessToken"]=token}).Build());
        var timeout=await Call(_=>hospital.GetVerifiedPatientAsync(tenant,patient,"50370000001"));
        Assert.Equal(504,timeout.Status);Assert.Equal("Hospital no respondió a tiempo. Comprueba la agenda antes de reintentar.",timeout.Title);
        // Any other client's timeout (Kapso, the assistant's model) is not Hospital's and keeps the generic answer.
        var other=await Run(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",new TimeoutException()));
        Assert.Equal(500,other.Status);Assert.DoesNotContain("Hospital",other.Title);
        var refused=await Run(new HospitalIntegrationException("hospital.http_409",System.Net.HttpStatusCode.Conflict));
        Assert.Equal(409,refused.Status);
        Assert.Contains(logs.Lines,line=>line.Contains("hospital.http_409")&&line.Contains(refused.Trace));
        Assert.DoesNotContain(logs.Lines,line=>line.Contains(patient.ToString())||line.Contains("/api/"));
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"backend","Program.cs")))root=root.Parent;
        Assert.Contains("app.Use(Hosting.Errors);",File.ReadAllText(Path.Combine(root!.FullName,"backend","Program.cs")));
    }
    // MR-37: the DataProtection keyring is protected by a certificate mounted outside /keys. Every case
    // goes through Hosting.AddRecepcionServices, the registration Program.cs uses; the certificate is
    // synthetic, generated here and written to a temporary directory (never to the repository).
    const string KeyringPassword="synthetic-keyring-pass";
    static string SyntheticCertificate(string dir)
    {
        using var rsa=RSA.Create(2048);
        var request=new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=recepcion-keyring-test",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(30));
        var path=Path.Combine(dir,"dataprotection.p12");File.WriteAllBytes(path,cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx,KeyringPassword));
        return path;
    }
    static IConfiguration KeyringConfig(string keys,string? cert,string? password=KeyringPassword)=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Database"]="Host=never-opened",["Auth:Authority"]="https://identity.example.test/realms/hospital",["KEY_DIRECTORY"]=keys,["DATAPROTECTION_CERT_PATH"]=cert,["DATAPROTECTION_CERT_PASSWORD"]=cert is null?null:password}).Build();
    static ServiceProvider Keyring(IConfiguration config){var services=new ServiceCollection().AddLogging();services.AddRecepcionServices(config,false);return services.BuildServiceProvider();}
    static string[] KeyFiles(string keys)=>Directory.GetFiles(keys,"*.xml").Select(File.ReadAllText).ToArray();
    [Fact] public void NewKeyIsEncryptedToTheCertificate() // MR-37 (a)
    {
        var keys=Directory.CreateTempSubdirectory("keys-").FullName;var config=KeyringConfig(keys,SyntheticCertificate(Directory.CreateTempSubdirectory("cert-").FullName));
        using var sp=Keyring(config);Hosting.ProtectKeyring(sp,config);
        sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("mr37").Protect("synthetic");
        var files=KeyFiles(keys);Assert.NotEmpty(files);
        Assert.All(files,xml=>{Assert.Contains("<encryptedSecret",xml);Assert.Contains("EncryptedKey",xml);Assert.DoesNotContain("<value>",xml);});
    }
    [Fact] public void AnotherProcessWithTheSameDirectoryAndCertificateUnprotects() // MR-37 (b)
    {
        var keys=Directory.CreateTempSubdirectory("keys-").FullName;var config=KeyringConfig(keys,SyntheticCertificate(Directory.CreateTempSubdirectory("cert-").FullName));
        string payload;
        using(var first=Keyring(config)){Hosting.ProtectKeyring(first,config);payload=first.GetRequiredService<IDataProtectionProvider>().CreateProtector("mr37").Protect("synthetic");}
        Assert.Contains(KeyFiles(keys),xml=>xml.Contains("EncryptedKey"));
        using var second=Keyring(config);
        Assert.Equal("synthetic",second.GetRequiredService<IDataProtectionProvider>().CreateProtector("mr37").Unprotect(payload));
    }
    [Fact] public void PayloadOfAnOldUnprotectedKeyIsStillRead() // MR-37 (c)
    {
        var keys=Directory.CreateTempSubdirectory("keys-").FullName;string payload;
        using(var before=Keyring(KeyringConfig(keys,null))){payload=before.GetRequiredService<IDataProtectionProvider>().CreateProtector("mr37").Protect("synthetic");}
        Assert.All(KeyFiles(keys),xml=>Assert.DoesNotContain("<encryptedSecret",xml));
        var config=KeyringConfig(keys,SyntheticCertificate(Directory.CreateTempSubdirectory("cert-").FullName));
        using var after=Keyring(config);Hosting.ProtectKeyring(after,config);
        Assert.Contains(KeyFiles(keys),xml=>xml.Contains("EncryptedKey"));
        Assert.Equal("synthetic",after.GetRequiredService<IDataProtectionProvider>().CreateProtector("mr37").Unprotect(payload));
    }
    /// <summary>MR-40 transition: a cut the API cannot read stops the start, naming the variable; read silently as the default it would
    /// leave every question the old version sent unanswered, or answer ones it never asked.</summary>
    [Theory]
    [InlineData("mañana a las 9", false)]
    [InlineData("2026-10-18T09:00:00", false)] // no zone: the server's would be guessed
    [InlineData("2026-10-18T09:00:00-06:00", true)]
    [InlineData("2026-10-18T15:00:00Z", true)]
    [InlineData(null, true)]
    public void AnUnreadableLegacyCutRefusesToStart(string? cut, bool starts)
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Database"]="Host=never-opened",["KEY_DIRECTORY"]=Directory.CreateTempSubdirectory("keys-").FullName,["AGENT_LEGACY_QUESTIONS_BEFORE"]=cut}).Build();
        var start=()=>new ServiceCollection().AddLogging().AddRecepcionServices(config,true);
        if(starts){start();return;}
        Assert.Contains("AGENT_LEGACY_QUESTIONS_BEFORE",Assert.Throws<InvalidOperationException>(start).Message);
    }
    [Fact] public void ProductionWithoutCertificateRefusesToStart() // MR-37 (d)
    {
        IHostEnvironment Env(string name)=>new Microsoft.Extensions.Hosting.Internal.HostingEnvironment{EnvironmentName=name};
        var keys=Directory.CreateTempSubdirectory("keys-").FullName;
        var refused=Assert.Throws<InvalidOperationException>(()=>Hosting.RequireKeyringCertificate(KeyringConfig(keys,null),Env("Production")));
        Assert.Contains("DATAPROTECTION_CERT_PATH",refused.Message);
        Hosting.RequireKeyringCertificate(KeyringConfig(keys,null),Env("Development"));
        // N5: every environment that is not Development needs the certificate, not only Production.
        Assert.Contains("DATAPROTECTION_CERT_PATH",Assert.Throws<InvalidOperationException>(()=>Hosting.RequireKeyringCertificate(KeyringConfig(keys,null),Env("Staging"))).Message);
        var cert=SyntheticCertificate(Directory.CreateTempSubdirectory("cert-").FullName);
        Hosting.RequireKeyringCertificate(KeyringConfig(keys,cert),Env("Production"));
        // A wrong password or a missing file fails with a message that names the setting, never the password.
        var wrong=Assert.Throws<InvalidOperationException>(()=>Hosting.KeyringCertificate(KeyringConfig(keys,cert,"not-the-password")));
        Assert.Contains("DATAPROTECTION_CERT_PASSWORD",wrong.Message);Assert.DoesNotContain("not-the-password",wrong.Message);
        Assert.Contains("DATAPROTECTION_CERT_PATH",Assert.Throws<InvalidOperationException>(()=>Hosting.KeyringCertificate(KeyringConfig(keys,Path.Combine(keys,"absent.p12")))).Message);
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"backend","Program.cs")))root=root.Parent;
        var program=File.ReadAllText(Path.Combine(root!.FullName,"backend","Program.cs"));
        Assert.Contains("Hosting.RequireKeyringCertificate(config, builder.Environment);",program);
        Assert.Contains("Hosting.ProtectKeyring(app.Services, config);",program);
    }
    [Fact] public void ANewProtectedKeyIsCreatedOnceWhenNoKeyIsEncrypted() // MR-37 (e)
    {
        var keys=Directory.CreateTempSubdirectory("keys-").FullName;
        using(var before=Keyring(KeyringConfig(keys,null))){before.GetRequiredService<IDataProtectionProvider>().CreateProtector("mr37").Protect("synthetic");}
        Assert.Single(KeyFiles(keys));
        var config=KeyringConfig(keys,SyntheticCertificate(Directory.CreateTempSubdirectory("cert-").FullName));
        using(var after=Keyring(config)){Hosting.ProtectKeyring(after,config);}
        Assert.Equal(2,KeyFiles(keys).Length);Assert.Single(KeyFiles(keys),xml=>xml.Contains("EncryptedKey"));
        using(var again=Keyring(config)){Hosting.ProtectKeyring(again,config);}
        Assert.Equal(2,KeyFiles(keys).Length);
    }
    // MR-37 I-1: a rollback to an image without the certificate leaves plaintext keys, one current and one
    // scheduled. The next start with the certificate must not keep using either: once the plaintext key
    // files are gone, what was written after that start still reads.
    [Fact] public void APlaintextDefaultLeftByARollbackIsReplacedAtStartup()
    {
        var keys=Directory.CreateTempSubdirectory("keys-").FullName;var config=KeyringConfig(keys,SyntheticCertificate(Directory.CreateTempSubdirectory("cert-").FullName));
        using(var first=Keyring(config))Hosting.ProtectKeyring(first,config);
        Guid scheduled;
        using(var rollback=Keyring(KeyringConfig(keys,null)))
        {
            var manager=rollback.GetRequiredService<Microsoft.AspNetCore.DataProtection.KeyManagement.IKeyManager>();var now=DateTimeOffset.UtcNow;
            manager.CreateNewKey(now,now.AddDays(90));
            scheduled=manager.CreateNewKey(now.AddDays(3),now.AddDays(93)).KeyId;
        }
        string payload;
        using(var after=Keyring(config)){Hosting.ProtectKeyring(after,config);payload=after.GetRequiredService<IDataProtectionProvider>().CreateProtector("mr37").Protect("synthetic");}
        var revocations=Directory.GetFiles(keys,"revocation-*.xml").Select(File.ReadAllText).ToArray();
        foreach(var file in Directory.GetFiles(keys,"key-*.xml").Where(f=>!File.ReadAllText(f).Contains("<encryptedSecret")))File.Delete(file);
        using var reader=Keyring(config);
        Assert.Equal("synthetic",reader.GetRequiredService<IDataProtectionProvider>().CreateProtector("mr37").Unprotect(payload));
        // The scheduled plaintext key protected nothing yet; it is revoked so it never becomes the default.
        Assert.Contains(revocations,xml=>xml.Contains(scheduled.ToString()));
    }
    // MR-37 I-2: Easypanel mounts files as text, so the certificate may arrive as the base64 of the .p12.
    [Fact] public void ABase64CertificateFileProtectsTheKeyring()
    {
        var certDir=Directory.CreateTempSubdirectory("cert-").FullName;var b64=Path.Combine(certDir,"dataprotection.p12.b64");
        File.WriteAllText(b64,Convert.ToBase64String(File.ReadAllBytes(SyntheticCertificate(certDir)),Base64FormattingOptions.InsertLineBreaks)+"\n");
        var keys=Directory.CreateTempSubdirectory("keys-").FullName;var config=KeyringConfig(keys,b64);
        string payload;
        using(var first=Keyring(config)){Hosting.ProtectKeyring(first,config);payload=first.GetRequiredService<IDataProtectionProvider>().CreateProtector("mr37").Protect("synthetic");}
        Assert.All(KeyFiles(keys),xml=>Assert.Contains("EncryptedKey",xml));
        using var second=Keyring(config);
        Assert.Equal("synthetic",second.GetRequiredService<IDataProtectionProvider>().CreateProtector("mr37").Unprotect(payload));
    }
    sealed class TimingOut:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",new TimeoutException());
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

// MR-16, regression of CrmDb.SaveChangesAsync against the real database: a row of another tenant, or of
// no tenant, is refused before any write; an Audit row is append-only.
public sealed class SaveChangesGuardTests : IAsyncLifetime
{
    readonly Guid a = Guid.NewGuid(), b = Guid.NewGuid(); DbContextOptions<CrmDb> options = null!;
    readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
    CrmDb Db(Guid tenant) => new(options, new TenantScope { Id = tenant }, protection);
    public async Task InitializeAsync()
    {
        options = new DbContextOptionsBuilder<CrmDb>().UseNpgsql(Environment.GetEnvironmentVariable("TEST_DATABASE") ?? throw new Exception("Use scripts/test-backend.sh")).Options;
        await using var db = Db(a); await db.Database.MigrateAsync();
        db.Tenants.Add(new Tenant { Id = a, Name = "Recepción A" }); db.Tenants.Add(new Tenant { Id = b, Name = "Recepción B" }); await db.SaveChangesAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask;
    async Task<int> Audits() { await using var db = Db(a); return await db.Audits.IgnoreQueryFilters().CountAsync(x => x.TenantId == a || x.TenantId == b); }

    [Fact] public async Task RowOfAnotherTenantOrOfNoTenantIsRefusedBeforeWriting()
    {
        await using (var db = Db(a))
        {
            db.Audits.Add(new Audit { TenantId = a, Action = "same.tenant" }); db.Audits.Add(new Audit { TenantId = b, Action = "other.tenant" });
            Assert.Equal("Tenant boundary violation", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync())).Message);
        }
        await using (var db = Db(Guid.Empty))
        {
            db.Audits.Add(new Audit { TenantId = Guid.Empty, Action = "no.tenant" });
            Assert.Equal("Tenant boundary violation", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync())).Message);
        }
        Assert.Equal(0, await Audits());
    }
    [Fact] public async Task AuditIsAppendOnly()
    {
        var id = Guid.NewGuid();
        await using (var db = Db(a)) { db.Audits.Add(new Audit { Id = id, TenantId = a, Actor = "synthetic", Action = "message.sent", Resource = "r" }); await db.SaveChangesAsync(); }
        await using (var db = Db(a))
        {
            (await db.Audits.SingleAsync(x => x.Id == id)).Action = "message.rewritten";
            Assert.Equal("Audit is append-only", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync())).Message);
        }
        await using (var db = Db(a))
        {
            db.Audits.Remove(await db.Audits.SingleAsync(x => x.Id == id));
            Assert.Equal("Audit is append-only", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync())).Message);
        }
        await using (var db = Db(a)) Assert.Equal("message.sent", (await db.Audits.SingleAsync(x => x.Id == id)).Action);
        Assert.Equal(1, await Audits());
    }
}
