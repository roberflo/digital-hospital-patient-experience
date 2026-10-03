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
    [Fact] public void TrustedOriginAndFixedIssuerOnly()
    {
        var values=HospitalConnectionRules.Validate(new("http://hospital-api:8080","https://hospital.example.com","recepcion","synthetic-secret"),Config());
        Assert.Equal("https://identity.example.com/realms/hospital/protocol/openid-connect/token",values["TokenEndpoint"]);
        Assert.Equal("true",values["UsePatientAgenda"]);
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
    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;return Task.FromResult(response(request));}
    }
}
