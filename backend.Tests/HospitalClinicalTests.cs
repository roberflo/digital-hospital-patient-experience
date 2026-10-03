using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Recepcion;
using Recepcion.Integrations;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

[CollectionDefinition("HospitalClinical", DisableParallelization = true)]
public sealed class HospitalClinicalCollection;

[Collection("HospitalClinical")]
public sealed class HospitalClinicalTests
{
    readonly Guid tenant = Guid.NewGuid(), patient = Guid.NewGuid(), actor = Guid.NewGuid();
    string Token(Guid? tenantOverride = null, string role = "Médicos") => "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { tenant_id = tenantOverride ?? tenant, sub = actor.ToString(), realm_access = new { roles = new[] { role } }, exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic";
    HospitalClinicalClient Client(Func<HttpRequestMessage,HttpResponseMessage> handler) => new(new HttpClient(new Handler(handler)), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { [$"Hospital:Tenants:{tenant}:BaseUrl"] = "https://hospital.example.invalid/", [$"Hospital:Tenants:{tenant}:AccessToken"] = "must-never-use-service-token" }).Build());
    static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    [Theory]
    [InlineData("agent", "doctor-1", "human", false)]
    [InlineData("admin", "doctor-1", "human", false)]
    [InlineData("doctor", "doctor-2", "human", false)]
    [InlineData("doctor", null, "human", false)]
    [InlineData("doctor", "doctor-1", "agent", false)]
    [InlineData("doctor", "doctor-1", "human", true)]
    [InlineData("doctor", "doctor-1", "closed", true)]
    public void ReadsRequireAssignedDoctor(string role, string? assigned, string status, bool allowed)
    {
        var user = new CurrentUser { Subject = "doctor-1", Role = role };
        var conversation = new Conversation { AssignedTo = assigned, Status = status };
        if (allowed) ClinicalEndpoints.RequireDoctor(user, conversation);
        else Assert.Throws<AccessDeniedException>(() => ClinicalEndpoints.RequireDoctor(user, conversation));
    }
    [Fact]
    public async Task DelegatesDoctorIdentityAndScopesPatientAndCursor()
    {
        var token = Token(); var calls = 0;
        var client = Client(request => {
            calls++; Assert.Equal(token, request.Headers.Authorization!.Parameter); Assert.Equal(HttpMethod.Get, request.Method);
            if (calls == 1) { Assert.Equal($"/v1/patients/{patient}", request.RequestUri!.AbsolutePath); return Json(new { patientId = patient, phone = "+503 7000-0001" }); }
            Assert.Equal($"/v1/patients/{patient}/timeline", request.RequestUri!.AbsolutePath);
            Assert.Contains("cursor=a%2Bb%2F%3D", request.RequestUri.Query);
            return Json(new { items = Array.Empty<object>(), recordOrigin = "migrated" });
        });
        var result = await client.ReadAsync(tenant, actor.ToString(), token, patient, "50370000001", "timeline", cursor: "a+b/=");
        Assert.Equal("migrated", result.GetProperty("recordOrigin").GetString()); Assert.Equal(2, calls);
    }
    [Theory][InlineData("phone")][InlineData("patient")]
    public async Task LinkMismatchStopsBeforeClinicalRead(string mismatch)
    {
        var calls = 0;
        var client = Client(_ => { calls++; return Json(new { patientId = mismatch == "patient" ? Guid.NewGuid() : patient, phone = mismatch == "phone" ? "50370000002" : "50370000001" }); });
        await Assert.ThrowsAsync<HospitalIntegrationException>(() => client.ReadAsync(tenant, actor.ToString(), Token(), patient, "50370000001", "allergies"));
        Assert.Equal(1, calls);
    }
    [Theory][InlineData("role")][InlineData("subject")][InlineData("tenant")]
    public async Task InvalidDelegationNeverUsesServiceCredentials(string mismatch)
    {
        var client = Client(_ => throw new Exception("Must not call Hospital"));
        await Assert.ThrowsAsync<HospitalIntegrationException>(() => client.ReadAsync(tenant, mismatch == "subject" ? Guid.NewGuid().ToString() : actor.ToString(), Token(mismatch == "tenant" ? Guid.NewGuid() : null, mismatch == "role" ? "Recepción" : "Médicos"), patient, "50370000001", "timeline"));
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task PrescriptionOwnerCheckedAndWithheldContentRedacted(bool matching)
    {
        var id = Guid.NewGuid();
        var client = Client(r => r.RequestUri!.AbsolutePath.EndsWith($"patients/{patient}") ? Json(new { patientId = patient, phone = "50370000001" }) : Json(new { prescriptionId = id, patientId = matching ? patient : Guid.NewGuid(), contentWithheld = true, lines = new[] { new { drugName = "Must not disclose" } } }));
        if (matching) { var result = await client.ReadAsync(tenant, actor.ToString(), Token(), patient, "50370000001", "prescriptions", id); Assert.Empty(result.GetProperty("lines").EnumerateArray()); }
        else await Assert.ThrowsAsync<HospitalIntegrationException>(() => client.ReadAsync(tenant, actor.ToString(), Token(), patient, "50370000001", "prescriptions", id));
    }
    [Fact]
    public async Task HospitalDenialDoesNotBecomeExpiredCrmSessionOrLeakBody()
    {
        var client = Client(_ => new(HttpStatusCode.Unauthorized) { Content = new StringContent("PRIVATE details") });
        var error = await Assert.ThrowsAsync<HospitalIntegrationException>(() => client.ReadAsync(tenant, actor.ToString(), Token(), patient, "50370000001", "timeline"));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode); Assert.DoesNotContain("PRIVATE", error.Message);
    }
    [Fact]
    public async Task ConcurrentFirstLoginRegistersOneMemberAndKeepsTenantIsolation()
    {
        var options = new DbContextOptionsBuilder<CrmDb>().UseNpgsql(Environment.GetEnvironmentVariable("TEST_DATABASE") ?? throw new InvalidOperationException("Run scripts/test-backend.sh")).Options;
        var protection = new EphemeralDataProtectionProvider();
        await using (var setup = new CrmDb(options, new TenantScope { Id = tenant }, protection))
        {
            await setup.Database.MigrateAsync();
            setup.Tenants.Add(new Tenant { Id = tenant, Name = "Synthetic clinical login" });
            await setup.SaveChangesAsync();
        }
        var requests = Enumerable.Range(0, 8).Select(async _ => {
            var scope = new TenantScope();
            await using var db = new CrmDb(options, scope, protection);
            var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", actor.ToString()), new Claim("tenant_id", tenant.ToString()), new Claim("role", "doctor") }, "test")) };
            return await Identity.Bind(ctx, db, scope, new CurrentUser());
        });
        Assert.All(await Task.WhenAll(requests), allowed => Assert.True(allowed));
        await using var verify = new CrmDb(options, new TenantScope { Id = tenant }, protection);
        Assert.Equal(1, await verify.Members.CountAsync(m => m.Subject == actor.ToString()));
        var otherTenant = Guid.NewGuid(); verify.Tenants.Add(new Tenant { Id = otherTenant, Name = "Other synthetic tenant" }); await verify.SaveChangesAsync();
        var other = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", actor.ToString()), new Claim("tenant_id", otherTenant.ToString()), new Claim("role", "doctor") }, "test")) };
        Assert.False(await Identity.Bind(other, verify, new TenantScope(), new CurrentUser()));
    }
    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request));
    }
}
