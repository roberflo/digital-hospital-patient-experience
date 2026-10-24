using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Recepcion;
using Xunit;

/// <summary>Criterio 32: the platform owner completes the contacts stored with 8 digits (before +503 was assumed) to 503 + digits and their
/// hash, on demand, simulated unless asked to apply. Synthetic data only; never run against a real database from here.</summary>
public sealed class ContactPhoneNormalizationTests : IAsyncLifetime
{
    const string Key = "synthetic-phone-hash-key-normalize";
    readonly AgentHarness h = new(phone: "50370000009");
    readonly IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["PHONE_HASH_KEY"] = Key }).Build();
    Guid clash, taken, alone; readonly Guid other = Guid.NewGuid(); Guid otherClash;
    // The BAJA of the number the old contact 70000002 becomes, sent from its wa_id: the completion leaves it exactly as it was.
    static readonly string OptOut = Rules.PhoneHash("50370000002", Key);
    public async Task InitializeAsync()
    {
        await h.Start();
        await using var db = h.NewDb(h.Scope);
        Contact Add(string phone) { var c = new Contact { TenantId = h.Scope.Id, Name = "Paciente Sintético", Phone = phone, PhoneHash = Rules.PhoneHash(phone, Key) }; db.Add(c); return c; }
        clash = Add("70000001").Id; taken = Add("50370000001").Id; alone = Add("70000002").Id;
        db.GeneralOptOuts.RemoveRange(db.GeneralOptOuts.Where(o => o.PhoneHash == OptOut)); // global: a test that failed its setup leaves it behind
        db.GeneralOptOuts.Add(new GeneralOptOut { PhoneHash = OptOut, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        // A second reception with its own conflict, for a run over every reception.
        await using var second = h.NewDb(new TenantScope { Id = other });
        second.Tenants.Add(new Tenant { Id = other, Name = "Hospital Sintético B" });
        foreach (var phone in new[] { "70000003", "50370000003" }) { var c = new Contact { TenantId = other, Name = "Paciente Sintético", Phone = phone, PhoneHash = Rules.PhoneHash(phone, Key) }; second.Add(c); if (phone.Length == 8) otherClash = c.Id; }
        await second.SaveChangesAsync();
    }
    public async Task DisposeAsync()
    {
        await using (var db = h.NewDb(new TenantScope())) { db.GeneralOptOuts.RemoveRange(db.GeneralOptOuts.Where(o => o.PhoneHash == OptOut)); await db.SaveChangesAsync(); } // the list is global
        await h.DisposeAsync();
    }

    static DefaultHttpContext Principal(string role, Guid? tenant = null)
    {
        var claims = new List<Claim> { new("sub", "owner-synthetic"), new("name", "Operadora Sintética"), new("realm_access", "{\"roles\":[\"" + role + "\"]}") };
        if (tenant is { } id) claims.Add(new("tenant_id", id.ToString()));
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
    }
    async Task<PlatformEndpoints.PhoneNormalization> Run(bool? apply, string role = "platform-owner", Guid? tenantClaim = null)
    {
        var scope = new TenantScope(); await using var db = h.NewDb(scope); // one scope for the context and the endpoint, as the request's DI gives them
        return await PlatformEndpoints.NormalizeContactPhones(h.Scope.Id, apply, Principal(role, tenantClaim), db, scope, config, CancellationToken.None);
    }
    async Task<Dictionary<Guid, (string Phone, string Hash)>> Contacts()
    {
        await using var db = h.NewDb(h.Scope);
        return await db.Contacts.AsNoTracking().Where(c => c.Id == clash || c.Id == taken || c.Id == alone).ToDictionaryAsync(c => c.Id, c => (c.Phone, c.PhoneHash));
    }

    [Fact]
    public async Task TheSimulationWritesNothingAndReportsTheConflictById()
    {
        var before = await Contacts();
        var result = await Run(apply: null);
        Assert.Equal((false, 1, 2), (result.Applied, result.Rewritten, result.AlreadyCanonical)); Assert.Equal([new(h.Scope.Id, clash)], result.Conflicts);
        Assert.Equal(before, await Contacts());
        await using var db = h.NewDb(h.Scope); // the read is audited, like the owner's doubts list
        Assert.Equal(("platform:owner-synthetic", h.Scope.Id.ToString()), (await db.Audits.Where(a => a.Action == "contact_phones.simulated").ToListAsync()).Select(a => (a.Actor, a.Resource)).Single());
    }

    [Fact]
    public async Task ApplyingCompletesTheLoneContactLeavesTheConflictAndIsIdempotent()
    {
        var before = await Contacts();
        var result = await Run(apply: true);
        Assert.Equal((true, 1), (result.Applied, result.Rewritten)); Assert.Equal([new(h.Scope.Id, clash)], result.Conflicts);
        var after = await Contacts();
        Assert.Equal(("50370000002", Rules.PhoneHash("50370000002", Key)), after[alone]);
        Assert.Equal((before[clash], before[taken]), (after[clash], after[taken])); // not merged, not changed
        await using (var db = h.NewDb(new TenantScope()))
            Assert.True(await db.GeneralOptOuts.AnyAsync(o => o.PhoneHash == OptOut)); // BAJA untouched
        Assert.Equal(0, (await Run(apply: true)).Rewritten);
    }

    /// <summary>Acts on the endpoint's first contact UPDATE: <paramref name="before"/> runs right before it (an edit, a new contact: deterministic,
    /// whatever the order of the checks), and <paramref name="after"/> fails the run right after it committed (a cancellation, a dropped connection).</summary>
    sealed class OnFirstUpdate(Func<Task>? before = null, Exception? after = null) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        bool started, finished;
        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData data, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!started && command.CommandText.Contains("UPDATE \"Contacts\"")) { started = true; if (before is not null) await before(); }
            return result;
        }
        public override ValueTask<int> NonQueryExecutedAsync(System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData data, int result, CancellationToken ct = default)
        {
            if (!finished && after is not null && command.CommandText.Contains("UPDATE \"Contacts\"")) { finished = true; throw after; }
            return ValueTask.FromResult(result);
        }
    }
    // I-1: a person edits the contact (or a message creates the same number) while the completion runs: the edit survives, the contact is
    // reported as a conflict, and the run goes on with its counts instead of failing as a whole.
    [Theory][InlineData("edited")][InlineData("created")]
    public async Task AConcurrentChangeIsNeverUndoneAndIsReported(string change)
    {
        var other = Guid.NewGuid();
        var interceptor = new OnFirstUpdate(async () =>
        {
            await using var db = h.NewDb(h.Scope);
            if (change == "edited") { var c = await db.Contacts.SingleAsync(x => x.Id == alone); c.Phone = "50370000099"; c.PhoneHash = Rules.PhoneHash("50370000099", Key); }
            else db.Add(new Contact { Id = other, TenantId = h.Scope.Id, Name = "Paciente Sintético", Phone = "50370000002", PhoneHash = Rules.PhoneHash("50370000002", Key) });
            await db.SaveChangesAsync();
        });
        var scope = new TenantScope(); await using var endpointDb = h.NewDb(scope, interceptor);

        var result = await PlatformEndpoints.NormalizeContactPhones(h.Scope.Id, true, Principal("platform-owner"), endpointDb, scope, config, CancellationToken.None);

        Assert.Contains(new PlatformEndpoints.PhoneConflict(h.Scope.Id, alone), result.Conflicts);
        Assert.Equal(change == "edited" ? "50370000099" : "70000002", (await Contacts())[alone].Phone);
    }

    // A run cut halfway (cancelled, a dropped connection) after a contact was rewritten: the reception's audit is already there, because a
    // second run rewrites 0 and could never record it.
    [Fact]
    public async Task AnInterruptedRunHasAlreadyAuditedTheReception()
    {
        var scope = new TenantScope(); await using var endpointDb = h.NewDb(scope, new OnFirstUpdate(after: new OperationCanceledException("synthetic cancellation")));

        await Assert.ThrowsAsync<OperationCanceledException>(() => PlatformEndpoints.NormalizeContactPhones(h.Scope.Id, true, Principal("platform-owner"), endpointDb, scope, config, CancellationToken.None));

        Assert.Equal("50370000002", (await Contacts())[alone].Phone); // the write before the cut stays
        await using var db = h.NewDb(h.Scope);
        Assert.Equal(1, await db.Audits.CountAsync(a => a.Action == "contact_phones.completed" && a.Resource == h.Scope.Id.ToString()));
    }

    // Only what the completion needs is read: a contact whose name or e-mail cannot be decrypted does not stop it.
    [Fact]
    public async Task OnlyThePhoneIsDecrypted()
    {
        await using (var db = h.NewDb(h.Scope)) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Contacts\" SET \"Name\" = {"not-a-ciphertext"} WHERE \"Id\" = {alone}");
        var result = await Run(apply: null);
        Assert.Equal(1, result.Rewritten);
    }

    // Without a reception, every reception is read. In a database of its own: «every reception» of the shared test database holds other
    // tests' contacts, encrypted with other key rings. The suite runs its tests one at a time (AgentGuardTests), so the variable can be swapped.
    [Fact]
    public async Task WithoutAReceptionEveryReceptionIsRead()
    {
        var shared = Environment.GetEnvironmentVariable("TEST_DATABASE")!; var name = "phones_" + Guid.NewGuid().ToString("N");
        async Task Admin(string sql) { await using var c = new Npgsql.NpgsqlConnection(shared); await c.OpenAsync(); await using var cmd = new Npgsql.NpgsqlCommand(sql, c); await cmd.ExecuteNonQueryAsync(); }
        await Admin($"CREATE DATABASE \"{name}\"");
        Environment.SetEnvironmentVariable("TEST_DATABASE", new Npgsql.NpgsqlConnectionStringBuilder(shared) { Database = name }.ConnectionString);
        try
        {
            await using var own = new AgentHarness(phone: "50370000009"); await own.Start();
            var b = Guid.NewGuid(); var canonicalOnly = Guid.NewGuid(); var expected = new List<PlatformEndpoints.PhoneConflict>();
            await using (var db = own.NewDb(new TenantScope { Id = canonicalOnly }))
            {
                db.Tenants.Add(new Tenant { Id = canonicalOnly, Name = "Hospital Sintético C" });
                db.Add(new Contact { TenantId = canonicalOnly, Name = "Paciente Sintético", Phone = "50370000006", PhoneHash = Rules.PhoneHash("50370000006", Key) });
                await db.SaveChangesAsync();
            }
            foreach (var tenant in new[] { own.Scope.Id, b })
            {
                await using var db = own.NewDb(new TenantScope { Id = tenant });
                if (tenant == b) db.Tenants.Add(new Tenant { Id = b, Name = "Hospital Sintético B" });
                foreach (var phone in new[] { "70000004", "50370000004", "70000005" }) { var c = new Contact { TenantId = tenant, Name = "Paciente Sintético", Phone = phone, PhoneHash = Rules.PhoneHash(phone, Key) }; db.Add(c); if (phone == "70000004") expected.Add(new(tenant, c.Id)); }
                await db.SaveChangesAsync();
            }

            var scope = new TenantScope(); await using var endpointDb = own.NewDb(scope);
            var result = await PlatformEndpoints.NormalizeContactPhones(null, null, Principal("platform-owner"), endpointDb, scope, config, CancellationToken.None);

            Assert.Equal((false, 2, 4), (result.Applied, result.Rewritten, result.AlreadyCanonical)); // 70000005 in each; 50370000004 in each, the harness's own contact and C's
            Assert.Equal(expected.OrderBy(c => c.TenantId), result.Conflicts.OrderBy(c => c.TenantId));
            await using var audits = own.NewDb(new TenantScope()); // every reception read is audited, also one with nothing to complete
            Assert.Equal(new[] { own.Scope.Id, b, canonicalOnly }.Select(t => t.ToString()).Order(), (await audits.Audits.IgnoreQueryFilters().Where(a => a.Action == "contact_phones.simulated").Select(a => a.Resource).ToListAsync()).Order());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_DATABASE", shared); Npgsql.NpgsqlConnection.ClearAllPools();
            await Admin($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    // Through the mapped route: authorization metadata, the 403 of the Errors middleware, and how apply and tenantId bind from the query.
    [Fact]
    public async Task TheRouteBindsApplyAndTenantAndRefusesAHospitalUser()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = Environment.GetEnvironmentVariable("TEST_DATABASE"), ["Auth:Authority"] = "https://identity.example.test/realms/hospital", ["KEY_DIRECTORY"] = Path.GetTempPath(), ["PHONE_HASH_KEY"] = Key });
        builder.Services.AddRecepcionServices(builder.Configuration, false);
        var app = builder.Build(); app.MapRecepcionApi();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == "/platform/contact-phones/complete");
        Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>());
        async Task<(int Status, string Body)> Post(DefaultHttpContext who, string query)
        {
            using var services = app.Services.CreateScope();
            who.RequestServices = services.ServiceProvider; who.Request.Method = "POST"; who.Request.QueryString = new(query); who.Response.Body = new MemoryStream();
            await Hosting.Errors(who, endpoint.RequestDelegate!);
            who.Response.Body.Position = 0; return (who.Response.StatusCode, await new StreamReader(who.Response.Body).ReadToEndAsync());
        }

        // The host has its own data protection, so it cannot read this harness's encrypted phones: it is pointed at an empty reception. A
        // 200 with nothing read is the proof that tenantId bound (without it every reception would be read); applied shows apply bound.
        var empty = Guid.NewGuid();
        Assert.Equal(403, (await Post(Principal("Administrador", h.Scope.Id), $"?tenantId={h.Scope.Id}&apply=true")).Status);
        Assert.Equal(403, (await Post(Principal("platform-owner", h.Scope.Id), $"?tenantId={h.Scope.Id}&apply=true")).Status); // acting for a reception
        foreach (var (query, applied) in new[] { ($"?tenantId={empty}", false), ($"?tenantId={empty}&apply=true", true) })
        {
            var (status, text) = await Post(Principal("platform-owner"), query);
            using var body = JsonDocument.Parse(text);
            Assert.Equal((200, applied, 0, 0), (status, body.RootElement.GetProperty("applied").GetBoolean(), body.RootElement.GetProperty("rewritten").GetInt32(), body.RootElement.GetProperty("alreadyCanonical").GetInt32()));
        }
        Assert.Equal("70000002", (await Contacts())[alone].Phone);
    }

    [Fact]
    public async Task OnlyThePlatformOwnerMayRunIt()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => Run(apply: true, role: "Administrador", tenantClaim: h.Scope.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Run(apply: true, tenantClaim: h.Scope.Id)); // an owner acting for a reception
    }
}
