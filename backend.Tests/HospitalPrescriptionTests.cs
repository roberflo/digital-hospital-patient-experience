using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
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
    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(send(request));
    }
}
