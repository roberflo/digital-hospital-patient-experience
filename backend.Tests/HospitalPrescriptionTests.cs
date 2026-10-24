using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Recepcion;
using Recepcion.Integrations;
using Xunit;

public sealed class HospitalPrescriptionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PdfRequiresMatchingPrescriptionOwnerEvenWhenPhonesMatch(bool matchingOwner)
    {
        var tenant=Guid.NewGuid();var patient=Guid.NewGuid();var prescription=Guid.NewGuid();
        var reads=0;var pdfs=0;
        var token="e30."+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new{tenant_id=tenant,exp=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()})).TrimEnd('=').Replace('+','-').Replace('/','_')+".synthetic";
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            [$"Hospital:Tenants:{tenant}:BaseUrl"]="https://hospital.example.invalid/",
            [$"Hospital:Tenants:{tenant}:AccessToken"]=token,
            [$"Hospital:Tenants:{tenant}:AllowClinicalDelivery"]="true",
            [$"Hospital:Tenants:{tenant}:UseReceptionBridge"]="true"
        }).Build();
        var handler=new Handler(request=>{
            var path=request.RequestUri!.AbsolutePath;
            if(path==$"/v1/patients/{patient}")return new(HttpStatusCode.OK){Content=JsonContent.Create(new{patientId=patient,phone="50370000001"})};
            if(path==$"/v1/reception/prescriptions/{prescription}"){
                reads++;
                return new(HttpStatusCode.OK){Content=JsonContent.Create(new{prescriptionId=prescription,patientId=matchingOwner?patient:Guid.NewGuid(),encounterId=Guid.NewGuid(),state="signed",signedAt=DateTimeOffset.UtcNow,lines=Array.Empty<object>(),contentWithheld=false})};
            }
            if(path==$"/v1/reception/prescriptions/{prescription}/pdf"){
                pdfs++;var content=new ByteArrayContent("%PDF-synthetic"u8.ToArray());content.Headers.ContentType=new("application/pdf");return new(HttpStatusCode.OK){Content=content};
            }
            throw new InvalidOperationException("Unexpected Hospital route: "+path);
        });
        var client=new HospitalClient(new HttpClient(handler),config);
        if(matchingOwner){var pdf=await client.GetPrescriptionPdfAsync(tenant,patient,"50370000001",prescription);Assert.Equal("%PDF-synthetic"u8.ToArray(),pdf);Assert.Equal(1,pdfs);}
        else{var error=await Assert.ThrowsAsync<HospitalIntegrationException>(()=>client.GetPrescriptionPdfAsync(tenant,patient,"50370000001",prescription));Assert.Equal("hospital.prescription_unavailable",error.Code);Assert.Equal(0,pdfs);}
        Assert.Equal(1,reads);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task LatestPrescriptionUsesSigningTimeAcrossAllPagesAndFailsClosedOnIncompleteHistory(bool cyclic)
    {
        var tenant=Guid.NewGuid();var patient=Guid.NewGuid();var first=Guid.NewGuid();var latest=Guid.NewGuid();
        var token="e30."+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new{tenant_id=tenant,exp=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()})).TrimEnd('=').Replace('+','-').Replace('/','_')+".synthetic";
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{[$"Hospital:Tenants:{tenant}:BaseUrl"]="https://hospital.example.invalid/",[$"Hospital:Tenants:{tenant}:AccessToken"]=token,[$"Hospital:Tenants:{tenant}:AllowClinicalDelivery"]="true",[$"Hospital:Tenants:{tenant}:UseReceptionBridge"]="true"}).Build();
        var pages=0;var reads=0;
        var handler=new Handler(request=>{
            var path=request.RequestUri!.AbsolutePath;
            if(path.StartsWith("/v1/patients/"))return new(HttpStatusCode.OK){Content=JsonContent.Create(new{patientId=patient,phone="50370000001"})};
            if(path.EndsWith("/list")){pages++;return new(HttpStatusCode.OK){Content=JsonContent.Create(new{prescriptionIds=new[]{pages==1?first:latest},nextCursor=pages==1||cyclic?"next":null})};}
            reads++;var id=Guid.Parse(path.Split('/').Last());return new(HttpStatusCode.OK){Content=JsonContent.Create(new{prescriptionId=id,patientId=patient,encounterId=Guid.NewGuid(),state="signed",signedAt=id==latest?DateTimeOffset.UtcNow:DateTimeOffset.UtcNow.AddDays(-1),lines=Array.Empty<object>(),contentWithheld=false})};
        });
        var client=new HospitalClient(new HttpClient(handler),config);
        if(cyclic)await Assert.ThrowsAsync<HospitalIntegrationException>(()=>client.GetLatestIssuedPrescriptionIdAsync(tenant,patient,"50370000001"));
        else Assert.Equal(latest,await client.GetLatestIssuedPrescriptionIdAsync(tenant,patient,"50370000001"));
        Assert.Equal(2,pages);Assert.Equal(2,reads);
    }
    // MR-6, MR-7: the service token is reused by the same tenant's credentials until 30 s before it expires,
    // checked against the tenant on every request, and the phone is verified once per «latest prescription».
    [Theory][InlineData("reused")][InlineData("expiring")][InlineData("other-tenant")]
    public async Task ServiceTokenIsReusedPerTenantCredentialAndPhoneIsVerifiedOncePerCall(string scenario)
    {
        var tenant=Guid.NewGuid();var patient=Guid.NewGuid();var first=Guid.NewGuid();var second=Guid.NewGuid();
        static string Token(Guid tenant,TimeSpan life)=>"e30."+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new{tenant_id=tenant,exp=DateTimeOffset.UtcNow.Add(life).ToUnixTimeSeconds()})).TrimEnd('=').Replace('+','-').Replace('/','_')+".synthetic";
        var token=Token(scenario=="other-tenant"?Guid.NewGuid():tenant,scenario=="expiring"?TimeSpan.FromSeconds(20):TimeSpan.FromHours(1));
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            [$"Hospital:Tenants:{tenant}:BaseUrl"]="https://hospital.example.invalid/",
            [$"Hospital:Tenants:{tenant}:TokenEndpoint"]="https://identity.example.invalid/realms/hospital/protocol/openid-connect/token",
            [$"Hospital:Tenants:{tenant}:ClientId"]="recepcion-service-shared-by-test",
            [$"Hospital:Tenants:{tenant}:ClientSecret"]="synthetic-secret",
            [$"Hospital:Tenants:{tenant}:AllowClinicalDelivery"]="true",[$"Hospital:Tenants:{tenant}:UseReceptionBridge"]="true"}).Build();
        var tokens=0;var patients=0;var hospital=0;
        var handler=new Handler(request=>{
            if(request.RequestUri!.Host=="identity.example.invalid"){tokens++;return new(HttpStatusCode.OK){Content=JsonContent.Create(new{access_token=token})};}
            hospital++;Assert.Equal(token,request.Headers.Authorization!.Parameter);
            var path=request.RequestUri.AbsolutePath;
            if(path==$"/v1/patients/{patient}"){patients++;return new(HttpStatusCode.OK){Content=JsonContent.Create(new{patientId=patient,phone="50370000001"})};}
            if(path.EndsWith("/list"))return new(HttpStatusCode.OK){Content=JsonContent.Create(new{prescriptionIds=new[]{first,second},nextCursor=(string?)null})};
            var id=Guid.Parse(path.Split('/').Last());
            return new(HttpStatusCode.OK){Content=JsonContent.Create(new{prescriptionId=id,patientId=patient,encounterId=Guid.NewGuid(),state="signed",signedAt=id==second?DateTimeOffset.UtcNow:DateTimeOffset.UtcNow.AddDays(-1),lines=Array.Empty<object>(),contentWithheld=false})};
        });
        var client=new HospitalClient(new HttpClient(handler),config);
        if(scenario=="other-tenant")
        {
            var error=await Assert.ThrowsAsync<HospitalIntegrationException>(()=>client.GetLatestIssuedPrescriptionIdAsync(tenant,patient,"50370000001"));
            Assert.Equal("hospital.token_tenant_or_expiry_invalid",error.Code);Assert.Equal(0,hospital);
            return;
        }
        Assert.Equal(second,await client.GetLatestIssuedPrescriptionIdAsync(tenant,patient,"50370000001"));
        Assert.Equal(1,patients);
        Assert.Equal(scenario=="reused"?1:4,tokens); // list + two reads + patient: one token, or one per request when it expires within 30 s
        if(scenario!="reused")return;
        // The same ClientId under another tenant never receives this tenant's cached token.
        var neighbour=Guid.NewGuid();token=Token(neighbour,TimeSpan.FromHours(1));
        var neighbourConfig=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            [$"Hospital:Tenants:{neighbour}:BaseUrl"]="https://hospital.example.invalid/",
            [$"Hospital:Tenants:{neighbour}:TokenEndpoint"]="https://identity.example.invalid/realms/hospital/protocol/openid-connect/token",
            [$"Hospital:Tenants:{neighbour}:ClientId"]="recepcion-service-shared-by-test",[$"Hospital:Tenants:{neighbour}:ClientSecret"]="synthetic-secret"}).Build();
        var before=tokens;
        await new HospitalClient(new HttpClient(handler),neighbourConfig).GetVerifiedPatientAsync(neighbour,patient,"50370000001");
        Assert.Equal(before+1,tokens);
    }
    // MR-6: a token Hospital rejects (401) is dropped, so the next request asks for a new one; the rejected request is not retried.
    [Fact]
    public async Task ATokenHospitalRejectsIsNotReused()
    {
        var tenant=Guid.NewGuid();var patient=Guid.NewGuid();var tokens=0;var hospital=0;
        var token="e30."+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new{tenant_id=tenant,exp=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()})).TrimEnd('=').Replace('+','-').Replace('/','_')+".synthetic";
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            [$"Hospital:Tenants:{tenant}:BaseUrl"]="https://hospital.example.invalid/",
            [$"Hospital:Tenants:{tenant}:TokenEndpoint"]="https://identity.example.invalid/realms/hospital/protocol/openid-connect/token",
            [$"Hospital:Tenants:{tenant}:ClientId"]=$"recepcion-service-{tenant:D}",[$"Hospital:Tenants:{tenant}:ClientSecret"]="synthetic-secret"}).Build();
        var client=new HospitalClient(new HttpClient(new Handler(request=>{
            if(request.RequestUri!.Host=="identity.example.invalid"){tokens++;return new(HttpStatusCode.OK){Content=JsonContent.Create(new{access_token=token})};}
            return ++hospital==1?new(HttpStatusCode.Unauthorized):new(HttpStatusCode.OK){Content=JsonContent.Create(new{patientId=patient,phone="50370000001"})};
        })),config);
        Assert.Equal("hospital.http_401",(await Assert.ThrowsAsync<HospitalIntegrationException>(()=>client.GetVerifiedPatientAsync(tenant,patient,"50370000001"))).Code);
        Assert.Equal(1,hospital);
        await client.GetVerifiedPatientAsync(tenant,patient,"50370000001");
        await client.GetVerifiedPatientAsync(tenant,patient,"50370000001");
        Assert.Equal(2,tokens);Assert.Equal(3,hospital);
    }
    // Criterio 17: Hospital stores the phone in E.164 (+50370000001); the WhatsApp sender is its wa_id without «+». Same patient.
    [Theory][InlineData("+50370000001")][InlineData("50370000001")]
    public Task AnE164HospitalPhoneVerifiesTheWaIdSender(string stored)=>Verifies(stored,"50370000001");
    // Criterio 27: a contact stored with 8 digits, before 503 was assumed, is read as 503 + digits when compared with Hospital.
    [Theory][InlineData("+50370000001")][InlineData("50370000001")][InlineData("7000-0001")]
    public Task AnOldEightDigitContactVerifiesAgainstHospital(string stored)=>Verifies(stored,"70000001");
    async Task Verifies(string stored,string contact)
    {
        var tenant=Guid.NewGuid();var patient=Guid.NewGuid();
        var token="e30."+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new{tenant_id=tenant,exp=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()})).TrimEnd('=').Replace('+','-').Replace('/','_')+".synthetic";
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            [$"Hospital:Tenants:{tenant}:BaseUrl"]="https://hospital.example.invalid/",[$"Hospital:Tenants:{tenant}:AccessToken"]=token}).Build();
        var client=new HospitalClient(new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=JsonContent.Create(new{patientId=patient,phone=stored})})),config);
        await client.GetVerifiedPatientAsync(tenant,patient,contact);
    }
    // INV-5: a phone written with its «+» already carries its country; 8 digits are only read as +503 without it. Santa Elena (+290) has 8 digits.
    [Fact]
    public async Task AnEightDigitE164IsNeverTakenForSalvadoran()
    {
        var tenant=Guid.NewGuid();var patient=Guid.NewGuid();
        var token="e30."+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new{tenant_id=tenant,exp=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()})).TrimEnd('=').Replace('+','-').Replace('/','_')+".synthetic";
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            [$"Hospital:Tenants:{tenant}:BaseUrl"]="https://hospital.example.invalid/",[$"Hospital:Tenants:{tenant}:AccessToken"]=token}).Build();
        var client=new HospitalClient(new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=JsonContent.Create(new{patientId=patient,phone="+29022345"})})),config);
        Assert.Equal("hospital.patient_phone_mismatch",(await Assert.ThrowsAsync<HospitalIntegrationException>(()=>client.GetVerifiedPatientAsync(tenant,patient,"50329022345"))).Code);
        Assert.Equal("29022345",HospitalClient.NormalizePhone("+290 2234 5"));Assert.Equal("29022345",Rules.StoredPhone("+29022345"));
    }
    // INV-5, criterio 16: the wa_id is already E.164; it is registered with its «+», never with a 503 put in front.
    [Theory][InlineData("29022345","+29022345")][InlineData("50370000001","+50370000001")]
    public async Task TheWaIdIsRegisteredAsItIs(string waId,string sent)
    {
        var tenant=Guid.NewGuid();string? phone=null;
        var token="e30."+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new{tenant_id=tenant,exp=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()})).TrimEnd('=').Replace('+','-').Replace('/','_')+".synthetic";
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            [$"Hospital:Tenants:{tenant}:BaseUrl"]="https://hospital.example.invalid/",[$"Hospital:Tenants:{tenant}:AccessToken"]=token}).Build();
        var client=new HospitalClient(new HttpClient(new Handler(r=>{
            phone=JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result).RootElement.GetProperty("phone").GetString();
            return new(HttpStatusCode.Created){Content=JsonContent.Create(new{created=true,patientId=Guid.NewGuid()})};
        })),config);
        await client.RegisterPatientAsync(tenant,new HospitalPatientRegistration("Ana Sintética","López Prueba",new DateOnly(1990,3,12),"female",waId,"Carlos Sintético","hermano","70000002"),Guid.NewGuid().ToString());
        Assert.Equal(sent,phone);
    }
    // Criterio 25: Hospital marks the phone for review ⇒ not verified, and no clinical read follows; a Hospital without the field behaves as before.
    [Theory][InlineData(true)][InlineData(false)][InlineData(null)]
    public async Task APhoneToReviewIsNotVerified(bool? needsReview)
    {
        var tenant=Guid.NewGuid();var patient=Guid.NewGuid();var prescription=Guid.NewGuid();var reads=0;
        var token="e30."+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new{tenant_id=tenant,exp=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()})).TrimEnd('=').Replace('+','-').Replace('/','_')+".synthetic";
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            [$"Hospital:Tenants:{tenant}:BaseUrl"]="https://hospital.example.invalid/",[$"Hospital:Tenants:{tenant}:AccessToken"]=token,
            [$"Hospital:Tenants:{tenant}:AllowClinicalDelivery"]="true",[$"Hospital:Tenants:{tenant}:UseReceptionBridge"]="true"}).Build();
        var client=new HospitalClient(new HttpClient(new Handler(request=>{
            if(request.RequestUri!.AbsolutePath==$"/v1/patients/{patient}")return new(HttpStatusCode.OK){Content=needsReview is {} review?JsonContent.Create(new{patientId=patient,phone="50370000001",phoneNeedsReview=review}):JsonContent.Create(new{patientId=patient,phone="50370000001"})};
            reads++;return new(HttpStatusCode.OK){Content=JsonContent.Create(new{prescriptionId=prescription,patientId=patient,encounterId=Guid.NewGuid(),state="signed",signedAt=DateTimeOffset.UtcNow,lines=Array.Empty<object>(),contentWithheld=false})};
        })),config);
        if(needsReview is true)
        {
            Assert.Equal("hospital.patient_phone_mismatch",(await Assert.ThrowsAsync<HospitalIntegrationException>(()=>client.GetVerifiedPatientAsync(tenant,patient,"50370000001"))).Code);
            await Assert.ThrowsAsync<HospitalIntegrationException>(()=>client.GetPrescriptionPdfAsync(tenant,patient,"50370000001",prescription));
            Assert.Equal(0,reads);
        }
        else await client.GetVerifiedPatientAsync(tenant,patient,"50370000001");
    }
    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(send(request));
    }
}
