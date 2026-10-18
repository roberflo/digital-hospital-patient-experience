using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Recepcion;
using Recepcion.Integrations;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

public class HospitalConnectionTests
{
    static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["HOSPITAL_ALLOWED_API_ORIGINS"]="https://hospital-api.example.com,http://hospital-api:8080",["Auth:Authority"]="https://identity.example.com/realms/hospital"
    }).Build();
    [Fact] public void EveryHospitalConnectsFromInstallationSettingsAlone()
    {
        var tenant=Guid.NewGuid();
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Auth:Authority"]="https://identity.example.com/realms/hospital",["HOSPITAL_API_URL"]="http://hospital-api:8080",["HOSPITAL_PUBLIC_URL"]="https://hospital.example.com",["HOSPITAL_SERVICE_CLIENT_SECRET"]="synthetic-secret"}).Build();
        var values=HospitalConnectionStore.Defaults(tenant,config);
        Assert.Equal($"recepcion-service-{tenant:D}",values["ClientId"]);
        Assert.Equal("https://identity.example.com/realms/hospital/protocol/openid-connect/token",values["TokenEndpoint"]);
        Assert.Equal("http://hospital-api:8080",values["BaseUrl"]);
        Assert.False(values.ContainsKey("UsePatientAgenda")); // MR-12: the option is gone, the patient agenda is the only one
        // Delivering prescriptions is never implied by being connected.
        Assert.False(values.ContainsKey("AllowClinicalDelivery"));Assert.False(values.ContainsKey("UseReceptionBridge"));
        Assert.NotEqual(values["ClientId"],HospitalConnectionStore.Defaults(Guid.NewGuid(),config)["ClientId"]);
        Assert.Empty(HospitalConnectionStore.Defaults(tenant,Config()));
    }
    [Fact] public void LinksIntoHospitalEnterThroughItsIdentityProvider()
    {
        var tenant=Guid.NewGuid();
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{[$"Hospital:Tenants:{tenant}:PublicUrl"]="https://hospital.example.com/ignored?x=1"}).Build();
        var client=new HospitalClient(new HttpClient(),config);
        Assert.Equal("https://hospital.example.com/api/auth/idp?returnTo=%2Fes%2Fcommercial",client.EntryUrl(tenant,"/es/commercial"));
        Assert.Null(client.EntryUrl(Guid.NewGuid(),"/es"));
    }
    [Fact] public void TrustedOriginAndFixedIssuerOnly()
    {
        var values=HospitalConnectionRules.Validate(new("http://hospital-api:8080","https://hospital.example.com","recepcion","synthetic-secret"),Config());
        Assert.Equal("https://identity.example.com/realms/hospital/protocol/openid-connect/token",values["TokenEndpoint"]);
        Assert.False(values.ContainsKey("UsePatientAgenda")); // MR-12: the option is gone, the patient agenda is the only one
        Assert.False(values.ContainsKey("AllowClinicalDelivery"));
        Assert.Null(values["AccessToken"]);
    }
    [Theory]
    [InlineData("http://169.254.169.254")]
    [InlineData("https://hospital-api.example.com.attacker.example")]
    [InlineData("https://user:pass@hospital-api.example.com")]
    [InlineData("https://hospital-api.example.com/other")]
    [InlineData("https://hospital-api.example.com?redirect=elsewhere")]
    public void UntrustedDestinationsAreRejectedBeforeSendingCredentials(string url)
        => Assert.Throws<ArgumentException>(()=>HospitalConnectionRules.Validate(new(url,"https://hospital.example.com","recepcion","synthetic-secret"),Config()));
    static IConfiguration WithPublic(string? url) => new ConfigurationBuilder().AddConfiguration(Config()).AddInMemoryCollection(new Dictionary<string,string?>{["HOSPITAL_PUBLIC_URL"]=url}).Build();
    [Fact] public void PublicUrlMustMatchTheInstallationsWhenDefined()
    {
        var config=WithPublic("https://hospital.example.com");
        Assert.Throws<ArgumentException>(()=>HospitalConnectionRules.Validate(new("http://hospital-api:8080","https://look-alike.example.net","recepcion","synthetic-secret"),config));
        Assert.Throws<ArgumentException>(()=>HospitalConnectionRules.Validate(new("http://hospital-api:8080","https://hospital.example.com:8443","recepcion","synthetic-secret"),config));
        Assert.Equal("https://hospital.example.com",HospitalConnectionRules.Validate(new("http://hospital-api:8080","https://hospital.example.com/","recepcion","synthetic-secret"),config)["PublicUrl"]);
    }
    [Fact] public void AnyHttpsPublicUrlIsAcceptedWhenTheInstallationDefinesNone()
        => Assert.Equal("https://other.example.org",HospitalConnectionRules.Validate(new("http://hospital-api:8080","https://other.example.org","recepcion","synthetic-secret"),WithPublic(null))["PublicUrl"]);
    [Fact] public void PublicHospitalRequiresHttpsInProduction()
        => Assert.Throws<ArgumentException>(()=>HospitalConnectionRules.Validate(new("https://hospital-api.example.com","http://hospital.example.com","recepcion","synthetic-secret"),Config()));
    [Fact] public async Task PatientSearchHasNarrowProjectionAndEscapedTerm()
    {
        var tenant=Guid.NewGuid(); var patient=Guid.NewGuid();
        var token="x."+Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{tenant_id=tenant,exp=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}))).TrimEnd('=').Replace('+','-').Replace('/','_')+".x";
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{[$"Hospital:Tenants:{tenant}:BaseUrl"]="https://hospital.example.com",[$"Hospital:Tenants:{tenant}:AccessToken"]=token}).Build();
        var handler=new Handler(r=>{
            Assert.Equal("/v1/patients/search",r.RequestUri!.AbsolutePath);
            Assert.Contains("term=Ana%20%26%20Maria",r.RequestUri.Query);
            return new(HttpStatusCode.OK){Content=JsonContent.Create(new{results=new[]{new{patientId=patient,displayName="Sintético",recordNumber="C-1",dui="PRIVATE",birthDate="2000-01-01"}}})};
        });
        var client=new HospitalClient(new HttpClient(handler),config);
        var hits=await client.SearchPatients(tenant,"name-tokens","Ana & Maria");
        Assert.Equal(patient,hits.Single().PatientId); Assert.DoesNotContain("PRIVATE",JsonSerializer.Serialize(hits));
        await Assert.ThrowsAsync<ArgumentException>(()=>client.SearchPatients(tenant,"phone","123456789"));
        Assert.Equal(1,handler.Calls);
    }
    // docs/platform-owner.md AC 12: the platform owner reads the setup and may save the connection of the
    // reception it acts for. The full save (Hospital accepts, row audited) is in PlatformOwnerTests.
    [Theory][InlineData("admin")][InlineData("platform")]
    public async Task AdministratorAndPlatformOwnerPassTheConnectionGuards(string role)
    {
        var user=new CurrentUser{Role=role,Subject=role=="platform"?"platform:owner-sub":"admin"};
        var setup=Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IValueHttpResult>(HospitalConnectionEndpoints.Setup(user,Config()));
        Assert.Contains("hospital-api.example.com",JsonSerializer.Serialize(setup.Value));
        // Past the guard, an untrusted destination is still refused before any credential is sent or anything is stored.
        await Assert.ThrowsAsync<ArgumentException>(()=>HospitalConnectionEndpoints.Save(new("http://169.254.169.254","https://hospital.example.com","recepcion","synthetic-secret"),user,new TenantScope{Id=Guid.NewGuid()},null!,Config(),null!,null!,default));
    }
    [Theory][InlineData("agent")][InlineData("doctor")][InlineData("")]
    public async Task OtherRolesCannotReadTheSetupOrSaveTheConnection(string role)
    {
        var user=new CurrentUser{Role=role,Subject="someone"};
        Assert.Throws<AccessDeniedException>(()=>HospitalConnectionEndpoints.Setup(user,Config()));
        await Assert.ThrowsAsync<AccessDeniedException>(()=>HospitalConnectionEndpoints.Save(new("http://hospital-api:8080","https://hospital.example.com","recepcion","synthetic-secret"),user,new TenantScope{Id=Guid.NewGuid()},null!,Config(),null!,null!,default));
    }
    // ---- MR-38: each `recepcion-service-<tenant>` authenticates with its own key (client-jwt), never with a shared secret ----
    const string Authority="https://identity.example.com/realms/hospital";
    static IConfiguration Installation(string? sharedSecret="synthetic-shared-secret") => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
        ["Auth:Authority"]=Authority,["KEYCLOAK_INTERNAL_ISSUER"]="http://keycloak:8080/realms/hospital",["HOSPITAL_API_URL"]="https://hospital-api.example.com",["HOSPITAL_SERVICE_CLIENT_SECRET"]=sharedSecret}).Build();
    static async Task<(CrmDb Db,Guid A,Guid B)> TwoHospitals()
    {
        var options=new DbContextOptionsBuilder<CrmDb>().UseNpgsql(Environment.GetEnvironmentVariable("TEST_DATABASE") ?? throw new InvalidOperationException("Run scripts/test-backend.sh")).Options;
        var db=new CrmDb(options,new TenantScope{Id=Guid.NewGuid()},new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        await db.Database.MigrateAsync();
        var a=Guid.NewGuid(); var b=Guid.NewGuid();
        db.Tenants.Add(new Tenant{Id=a,Name="Hospital sintético A"}); db.Tenants.Add(new Tenant{Id=b,Name="Hospital sintético B"}); await db.SaveChangesAsync();
        return (db,a,b);
    }
    static string ServiceToken(Guid tenant) => "x."+Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{tenant_id=tenant,exp=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}))).TrimEnd('=').Replace('+','-').Replace('/','_')+".x";
    static byte[] B64(string value){value=value.Replace('-','+').Replace('_','/');return Convert.FromBase64String(value.PadRight((value.Length+3)/4*4,'='));}
    static JsonElement Keys(Guid tenant,CrmDb db,IConfiguration config) => JsonSerializer.SerializeToElement(HospitalConnectionEndpoints.Jwks(tenant,new HospitalConnectionStore(db,config))).GetProperty("keys");
    /// <summary>Every token request the client makes, as form fields; Hospital's API answers 200 to everything else.</summary>
    sealed class Keycloak(Func<Dictionary<string,string>,HttpResponseMessage> token):HttpMessageHandler
    {
        public readonly List<Dictionary<string,string>> TokenRequests=new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(!request.RequestUri!.AbsolutePath.EndsWith("/protocol/openid-connect/token")) return new(HttpStatusCode.OK){Content=JsonContent.Create(new{displayName="Sintético",timeZone="UTC"})};
            var form=(await request.Content!.ReadAsStringAsync(ct)).Split('&').Select(x=>x.Split('=')).ToDictionary(x=>Uri.UnescapeDataString(x[0]),x=>Uri.UnescapeDataString(x[1].Replace('+',' ')));
            TokenRequests.Add(form); return token(form);
        }
    }
    static HttpResponseMessage Issued(Guid tenant) => new(HttpStatusCode.OK){Content=JsonContent.Create(new{access_token=ServiceToken(tenant)})};
    static HttpResponseMessage InvalidClient() => new(HttpStatusCode.BadRequest){Content=JsonContent.Create(new{error="invalid_client",error_description="Invalid client or Invalid client credentials"})};
    static (JsonElement Header,JsonElement Claims) Assertion(Dictionary<string,string> form,Guid tenant,JsonElement jwks)
    {
        Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer",form["client_assertion_type"]);
        var parts=form["client_assertion"].Split('.');
        var header=JsonSerializer.Deserialize<JsonElement>(B64(parts[0])); var claims=JsonSerializer.Deserialize<JsonElement>(B64(parts[1]));
        Assert.Equal("ES256",header.GetProperty("alg").GetString());
        var jwk=jwks.EnumerateArray().Single(k=>k.GetProperty("kid").GetString()==header.GetProperty("kid").GetString());
        using var key=System.Security.Cryptography.ECDsa.Create(new System.Security.Cryptography.ECParameters{Curve=System.Security.Cryptography.ECCurve.NamedCurves.nistP256,Q=new(){X=B64(jwk.GetProperty("x").GetString()!),Y=B64(jwk.GetProperty("y").GetString()!)}});
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(parts[0]+"."+parts[1]),B64(parts[2]),System.Security.Cryptography.HashAlgorithmName.SHA256),"the assertion verifies with this hospital's published key");
        Assert.Equal($"recepcion-service-{tenant:D}",claims.GetProperty("iss").GetString()); Assert.Equal($"recepcion-service-{tenant:D}",claims.GetProperty("sub").GetString());
        Assert.Equal(Authority,claims.GetProperty("aud").GetString()); // the spike: Keycloak accepts only its public issuer, even when reached by the internal URL
        Assert.InRange(claims.GetProperty("exp").GetInt64()-DateTimeOffset.UtcNow.ToUnixTimeSeconds(),1,60);
        Assert.False(string.IsNullOrEmpty(claims.GetProperty("jti").GetString()));
        return (header,claims);
    }
    [Fact] public async Task EachHospitalSignsWithItsOwnKeyAndNeverSendsTheSharedSecret()
    {
        var (db,a,b)=await TwoHospitals(); await using var _=db; var config=Installation();
        var keycloak=new Keycloak(form=>Issued(Guid.Parse(form["client_id"]["recepcion-service-".Length..])));
        var client=new HospitalClient(new HttpClient(keycloak),config,new HospitalConnectionStore(db,config));
        await client.GetClinicAsync(a); await client.GetClinicAsync(b);
        Assert.Equal(2,keycloak.TokenRequests.Count);
        Assert.All(keycloak.TokenRequests,form=>{Assert.False(form.ContainsKey("client_secret"));Assert.Equal("client_credentials",form["grant_type"]);});
        var keysA=Keys(a,db,config); var keysB=Keys(b,db,config);
        var (headerA,claimsA)=Assertion(keycloak.TokenRequests[0],a,keysA); var (headerB,_)=Assertion(keycloak.TokenRequests[1],b,keysB);
        Assert.NotEqual(headerA.GetProperty("kid").GetString(),headerB.GetProperty("kid").GetString());
        Assert.NotEqual(keycloak.TokenRequests[0]["client_assertion"].Split('.')[2],keycloak.TokenRequests[1]["client_assertion"].Split('.')[2]);
        // A's JWKS carries A's key only: B's assertion does not verify against it.
        Assert.Throws<InvalidOperationException>(()=>Assertion(keycloak.TokenRequests[1],b,keysA));
        Assert.DoesNotContain(keysB[0].GetProperty("x").GetString()!,keysA.GetRawText());
        // Public halves only.
        Assert.All(keysA.EnumerateArray().Concat(keysB.EnumerateArray()),k=>{Assert.False(k.TryGetProperty("d",out var __));Assert.Equal("EC",k.GetProperty("kty").GetString());Assert.Equal("P-256",k.GetProperty("crv").GetString());});
        // A hospital Recepción does not know publishes nothing.
        Assert.Equal(0,Keys(Guid.NewGuid(),db,config).GetArrayLength());
    }
    [Fact] public async Task RotatingOneHospitalsKeyLeavesTheOtherHospitalsKeyAlone()
    {
        var (db,a,b)=await TwoHospitals(); await using var _=db; var config=Installation();
        var keycloak=new Keycloak(form=>Issued(Guid.Parse(form["client_id"]["recepcion-service-".Length..])));
        await new HospitalClient(new HttpClient(keycloak),config,new HospitalConnectionStore(db,config)).GetClinicAsync(a);
        await new HospitalClient(new HttpClient(keycloak),config,new HospitalConnectionStore(db,config)).GetClinicAsync(b);
        var beforeA=Keys(a,db,config).GetRawText(); var beforeB=Keys(b,db,config).GetRawText();
        await new HospitalConnectionStore(db,config).SigningKeyAsync(a,rotate:true);
        Assert.NotEqual(beforeA,Keys(a,db,config).GetRawText());
        Assert.Equal(beforeB,Keys(b,db,config).GetRawText());
        // The next token of A is signed with the new key, not answered from the cache of the old one.
        await new HospitalClient(new HttpClient(keycloak),config,new HospitalConnectionStore(db,config)).GetClinicAsync(a);
        Assert.Equal(3,keycloak.TokenRequests.Count);
        Assertion(keycloak.TokenRequests[2],a,Keys(a,db,config));
    }
    // ponytail transition (deploy step 2 of 3): until the reconciler turns the account into client-jwt, Keycloak answers invalid_client.
    [Fact] public async Task TheSharedSecretIsTriedOnlyAfterKeycloakRejectsTheKeyAsInvalidClient()
    {
        var (db,a,_)=await TwoHospitals(); await using var __=db; var config=Installation();
        var keycloak=new Keycloak(form=>form.ContainsKey("client_secret")?Issued(a):InvalidClient());
        await new HospitalClient(new HttpClient(keycloak),config,new HospitalConnectionStore(db,config)).GetClinicAsync(a);
        Assert.Equal(2,keycloak.TokenRequests.Count);
        Assert.True(keycloak.TokenRequests[0].ContainsKey("client_assertion")); Assert.False(keycloak.TokenRequests[0].ContainsKey("client_secret"));
        Assert.Equal("synthetic-shared-secret",keycloak.TokenRequests[1]["client_secret"]); Assert.False(keycloak.TokenRequests[1].ContainsKey("client_assertion"));
        // Any other refusal is not a reason to send the secret.
        var (db2,b,___)=await TwoHospitals(); await using var ____=db2;
        var down=new Keycloak(_=>new(HttpStatusCode.ServiceUnavailable));
        var failure=await Assert.ThrowsAsync<HospitalIntegrationException>(()=>new HospitalClient(new HttpClient(down),config,new HospitalConnectionStore(db2,config)).GetClinicAsync(b));
        Assert.Equal("hospital.token_request_failed",failure.Code); Assert.Single(down.TokenRequests); Assert.False(down.TokenRequests[0].ContainsKey("client_secret"));
        // Without the variable (deploy step 3) invalid_client is final.
        var noSecret=Installation(sharedSecret:null); var rejected=new Keycloak(_=>InvalidClient());
        var final=await Assert.ThrowsAsync<HospitalIntegrationException>(()=>new HospitalClient(new HttpClient(rejected),noSecret,new HospitalConnectionStore(db2,noSecret)).GetClinicAsync(b));
        Assert.Equal("hospital.token_request_failed",final.Code); Assert.Single(rejected.TokenRequests);
    }
    [Fact] public void HospitalsConnectWithoutTheSharedSecret()
    {
        var tenant=Guid.NewGuid(); var values=HospitalConnectionStore.Defaults(tenant,Installation(sharedSecret:null));
        Assert.Equal($"recepcion-service-{tenant:D}",values["ClientId"]); Assert.Equal("https://hospital-api.example.com",values["BaseUrl"]);
        Assert.False(values.ContainsKey("ClientSecret"));
    }
    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;return Task.FromResult(response(request));}
    }
}
