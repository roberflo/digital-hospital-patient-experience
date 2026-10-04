using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Recepcion;
using Recepcion.Integrations;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

public sealed class HospitalIdentitySyncTests : IAsyncLifetime
{
    readonly TenantScope scope = new() { Id = Guid.NewGuid() };
    readonly TenantScope otherScope = new() { Id = Guid.NewGuid() };
    readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
    DbContextOptions<CrmDb> options = null!; CrmDb db = null!;
    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_DATABASE");
        if (string.IsNullOrEmpty(connection)) throw new InvalidOperationException("Run scripts/test-backend.sh to provide the isolated PostgreSQL database.");
        options = new DbContextOptionsBuilder<CrmDb>().UseNpgsql(connection).Options;
        db = new(options, scope, protection); await db.Database.MigrateAsync();
        db.Tenants.Add(new Tenant { Id = scope.Id, Name = "Hospital · configura tu nombre", TimeZone = "America/El_Salvador" });
        db.Tenants.Add(new Tenant { Id = otherScope.Id, Name = "Other synthetic", TimeZone = "America/El_Salvador" });
        await db.SaveChangesAsync();
    }
    public async Task DisposeAsync() { if (db != null) await db.DisposeAsync(); }

    HospitalClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond, out Handler handler)
    {
        var token = "x." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { tenant_id = scope.Id, exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".x";
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"Hospital:Tenants:{scope.Id}:BaseUrl"] = "https://hospital.example.com", [$"Hospital:Tenants:{scope.Id}:AccessToken"] = token }).Build();
        handler = new Handler(respond);
        return new HospitalClient(new HttpClient(handler), cfg);
    }
    static HttpResponseMessage Clinic(string? name, string? zone) => new(HttpStatusCode.OK) { Content = JsonContent.Create(new { displayName = name, timeZone = zone }) };
    Task<bool> Sync(HospitalClient h) => HospitalIdentitySync.Run(db, scope, new CurrentUser { Subject = "synthetic-admin" }, h, NullLogger.Instance);
    async Task<Tenant> Row(Guid id) { await using var fresh = new CrmDb(options, new TenantScope { Id = id }, protection); return await fresh.Tenants.AsNoTracking().SingleAsync(x => x.Id == id); }
    Task<int> Audits() => db.Audits.CountAsync(x => x.Action == "hospital.identity_synced");

    [Fact] public async Task UpdatesNameAndZoneWithOneAuditThenIsIdempotent()
    {
        var h = Client(r => { Assert.Equal("/v1/clinic", r.RequestUri!.AbsolutePath); return Clinic("  Hospital Sintético  ", "America/Mexico_City"); }, out _);
        Assert.True(await Sync(h));
        var row = await Row(scope.Id);
        Assert.Equal("Hospital Sintético", row.Name); Assert.Equal("America/Mexico_City", row.TimeZone);
        Assert.Equal(1, await Audits());
        Assert.False(await Sync(h));
        Assert.Equal(1, await Audits());
    }
    [Theory][InlineData(404)][InlineData(503)]
    public async Task HospitalErrorsLeaveTenantUnchanged(int status)
    {
        var h = Client(_ => new((HttpStatusCode)status), out _);
        Assert.False(await Sync(h));
        Assert.Equal("Hospital · configura tu nombre", (await Row(scope.Id)).Name); Assert.Equal(0, await Audits());
    }
    [Fact] public async Task NetworkFailureLeavesTenantUnchanged()
    {
        var h = Client(_ => throw new HttpRequestException("down"), out _);
        Assert.False(await Sync(h));
        Assert.Equal("Hospital · configura tu nombre", (await Row(scope.Id)).Name);
    }
    [Fact] public async Task UnconfiguredHospitalIsSwallowed()
        => Assert.False(await Sync(new HospitalClient(new HttpClient(), new ConfigurationBuilder().Build())));
    [Fact] public async Task InvalidZoneKeepsZoneButSyncsName()
    {
        Assert.True(await Sync(Client(_ => Clinic("Hospital Sintético", "Not/AZone"), out _)));
        var row = await Row(scope.Id); Assert.Equal("Hospital Sintético", row.Name); Assert.Equal("America/El_Salvador", row.TimeZone);
    }
    [Theory][InlineData("   ")][InlineData(null)][InlineData("OVERSIZED")]
    public async Task BlankOrOversizedNameKeepsNameButSyncsZone(string? name)
    {
        if (name == "OVERSIZED") name = new string('x', 201);
        Assert.True(await Sync(Client(_ => Clinic(name, "America/Mexico_City"), out _)));
        var row = await Row(scope.Id); Assert.Equal("Hospital · configura tu nombre", row.Name); Assert.Equal("America/Mexico_City", row.TimeZone);
    }
    [Fact] public async Task NeverTouchesAnotherTenant()
    {
        await Sync(Client(_ => Clinic("Hospital Sintético", "America/Mexico_City"), out _));
        var other = await Row(otherScope.Id); Assert.Equal("Other synthetic", other.Name); Assert.Equal("America/El_Salvador", other.TimeZone);
    }
    [Fact] public void ManualNameAndZoneAreIgnoredWhenConnectedAndAppliedWhenNot()
    {
        var input = new SettingsInput("Manual", "guía", "America/Mexico_City", true, null);
        var connected = new Tenant { Name = "Hospital", TimeZone = "America/El_Salvador" };
        CrmEndpoints.ApplySettings(connected, input, true);
        Assert.Equal("Hospital", connected.Name); Assert.Equal("America/El_Salvador", connected.TimeZone); Assert.Equal("guía", connected.Guide); Assert.True(connected.AgentEnabled);
        var free = new Tenant { Name = "Hospital" };
        CrmEndpoints.ApplySettings(free, input, false);
        Assert.Equal("Manual", free.Name); Assert.Equal("America/Mexico_City", free.TimeZone);
    }
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
