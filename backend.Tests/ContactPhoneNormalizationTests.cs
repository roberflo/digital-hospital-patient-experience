using System.Security.Claims;
using Microsoft.AspNetCore.Http;
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
    Guid clash, taken, alone; readonly string optOut = "synthetic-opt-out-" + Guid.NewGuid();
    public async Task InitializeAsync()
    {
        await h.Start();
        await using var db = h.NewDb(h.Scope);
        Contact Add(string phone) { var c = new Contact { TenantId = h.Scope.Id, Name = "Paciente Sintético", Phone = phone, PhoneHash = Rules.PhoneHash(phone, Key) }; db.Add(c); return c; }
        clash = Add("70000001").Id; taken = Add("50370000001").Id; alone = Add("70000002").Id;
        db.GeneralOptOuts.Add(new GeneralOptOut { PhoneHash = optOut, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }
    public Task DisposeAsync() => h.DisposeAsync().AsTask();

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
        Assert.Equal((false, 1), (result.Applied, result.Rewritten)); Assert.Equal([new(h.Scope.Id, clash)], result.Conflicts);
        Assert.Equal(before, await Contacts());
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
            Assert.True(await db.GeneralOptOuts.AnyAsync(o => o.PhoneHash == optOut)); // BAJA untouched
        Assert.Equal(0, (await Run(apply: true)).Rewritten);
    }

    /// <summary>Runs <paramref name="act"/> once, right after the endpoint checks whether the new hash is taken and before it writes.</summary>
    sealed class BetweenCheckAndWrite(Func<Task> act) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        bool done;
        public override async ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData data, System.Data.Common.DbDataReader result, CancellationToken ct = default)
        {
            if (!done && command.CommandText.Contains("EXISTS")) { done = true; await act(); }
            return result;
        }
    }
    // I-1: a person edits the contact (or a message creates the same number) while the completion runs: the edit survives, the contact is
    // reported as a conflict, and the run goes on with its counts instead of failing as a whole.
    [Theory][InlineData("edited")][InlineData("created")]
    public async Task AConcurrentChangeIsNeverUndoneAndIsReported(string change)
    {
        var other = Guid.NewGuid();
        var interceptor = new BetweenCheckAndWrite(async () =>
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

    [Fact]
    public async Task OnlyThePlatformOwnerMayRunIt()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => Run(apply: true, role: "Administrador", tenantClaim: h.Scope.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Run(apply: true, tenantClaim: h.Scope.Id)); // an owner acting for a reception
    }
}
