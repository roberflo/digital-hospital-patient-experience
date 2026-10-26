using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>docs/usage-report.md: what each clinic used in a month, counted for the platform owner. Counts only.</summary>
public sealed class UsageReportTests : IAsyncLifetime
{
    CrmDb db = null!; readonly TenantScope scope = new(); readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
    readonly string sub = Guid.NewGuid().ToString(); readonly string tag = "zz-usage-" + Guid.NewGuid().ToString("N");
    CurrentUser Owner => new() { Role = "platform", Subject = "platform:" + sub, Name = "Dueña Sintética" };
    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<CrmDb>().UseNpgsql(Environment.GetEnvironmentVariable("TEST_DATABASE") ?? throw new Exception("Use scripts/test-backend.sh")).Options;
        db = new CrmDb(options, scope, protection); await db.Database.MigrateAsync();
    }
    public Task DisposeAsync() => db.DisposeAsync().AsTask();

    sealed record Seeded(Guid Id, Conversation[] Conversations);
    /// <summary>A clinic with one real enabled number and <paramref name="conversations"/> conversations on it.</summary>
    async Task<Seeded> Clinic(string suffix, string zone = "America/El_Salvador", int conversations = 1)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = tag + "-" + suffix, TimeZone = zone }; db.Tenants.Add(tenant); await db.SaveChangesAsync();
        scope.Id = tenant.Id;
        var channel = new Channel { TenantId = tenant.Id, Name = "WhatsApp", PhoneNumberId = Guid.NewGuid().ToString("N"), Enabled = true }; db.Add(channel);
        var rows = Enumerable.Range(0, conversations).Select(i =>
        {
            var contact = new Contact { TenantId = tenant.Id, Name = "Paciente Secreto " + suffix, Phone = "50370009" + i.ToString("000"), PhoneHash = Guid.NewGuid().ToString() };
            db.Add(contact);
            var conversation = new Conversation { TenantId = tenant.Id, ChannelId = channel.Id, ContactId = contact.Id, Status = "agent" }; db.Add(conversation);
            return conversation;
        }).ToArray();
        await db.SaveChangesAsync();
        return new(tenant.Id, rows);
    }
    void Message(Conversation c, string sender, DateTimeOffset at, string status = "sent", bool history = false, string type = "text")
        => db.Add(new Message { TenantId = c.TenantId, ConversationId = c.Id, Sender = sender, Type = type, Status = status, IsHistory = history, Body = "cuerpo-secreto", CreatedAt = at.ToUniversalTime() });
    void Step(Conversation c, string kind, DateTimeOffset at, string role = "agent_ai")
        => db.Activities.Add(new Activity { TenantId = c.TenantId, ConversationId = c.Id, ContactId = c.ContactId, Kind = kind, ActorRole = role, Actor = "x", Body = "actividad-secreta", CreatedAt = at });
    static readonly DateTimeOffset March = new(2026, 3, 10, 15, 0, 0, TimeSpan.Zero);

    // ---- AC 4, 5, 6, 8, 10, 23 (sustituye a 11) ----
    [Fact] public async Task CountsEachClinicsMonthByClass()
    {
        var a = await Clinic("a", conversations: 3); var (c1, c2, c3) = (a.Conversations[0], a.Conversations[1], a.Conversations[2]);
        db.Add(new Channel { TenantId = a.Id, Name = "Pausado", PhoneNumberId = Guid.NewGuid().ToString("N"), Enabled = false });
        db.Add(new Channel { TenantId = a.Id, Name = "Demo", PhoneNumberId = "demo-" + Guid.NewGuid().ToString("N"), Enabled = true }); // not a connection
        Message(c1, "patient", March, "received"); Message(c1, "patient", March, "received"); Message(c2, "patient", March, "received");
        Message(c2, "patient", March, "received", history: true); // imported on connecting: not this month's use
        Message(c1, "patient", March.AddMonths(-1), "received");  // last month
        Message(c1, "agent", March); Message(c2, "agent", March); Message(c1, "agent", March, "failed"); // a failed send did not go out
        Message(c1, "human", March); Message(c3, "human", March, "uncertain"); Message(c3, "human", March, history: true);
        Message(c1, "system", March, type: "template"); Message(c2, "system", March, "failed", type: "template");
        Step(c1, "response", March); Step(c1, "appointment", March); Step(c1, "appointment", March); Step(c1, "prescription_delivered", March);
        Step(c2, "patient_registered", March);
        Step(c3, "appointment", March, "admin");  // a person's booking is not the agent's
        Step(c3, "guard", March, "system");       // nor is the system's note about a turn
        Step(c3, "appointment", March.AddMonths(1));
        await db.SaveChangesAsync();
        var b = await Clinic("b");
        Message(b.Conversations[0], "patient", March, "received"); Step(b.Conversations[0], "appointment", March); await db.SaveChangesAsync();
        scope.Id = Guid.Empty; // the owner reads it without choosing a reception

        var report = await UsageReport.Read("2026-03", Owner, db, scope, CancellationToken.None);

        Assert.Equal("2026-03", report.Month);
        Assert.True(report.AiMetered);
        var mine = report.Tenants.Where(x => x.Name.StartsWith(tag, StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { a.Id, b.Id }, mine.Select(x => x.Id));
        Assert.Equal(new UsageCounts(2, 1, 3, 2, 2, 1, 2, 2, 1, 1, 0, 0, 0, 0), mine[0].Usage); // metered, no model calls: zeros
        Assert.Equal(new UsageCounts(1, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0), mine[1].Usage);
        // The wire, as minimal APIs serialize it: the contract's fields and nothing a person could be read from.
        var wire = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(wire);
        Assert.Equal(new[] { "month", "aiMetered", "tenants", "totals" }, doc.RootElement.EnumerateObject().Select(x => x.Name));
        Assert.All(doc.RootElement.GetProperty("tenants").EnumerateArray(), row => Assert.Equal(new[] { "id", "name", "usage" }, row.EnumerateObject().Select(x => x.Name)));
        Assert.Equal(new[] { "numbersConnected", "numbersEnabled", "patientMessages", "agentReplies", "humanReplies", "templatesSent", "agentConversations", "appointments", "registrations", "prescriptions", "aiCalls", "inputTokens", "outputTokens", "cachedInputTokens" },
            doc.RootElement.GetProperty("totals").EnumerateObject().Select(x => x.Name));
        foreach (var secret in new[] { "Paciente Secreto", "5037000", "cuerpo-secreto", "actividad-secreta" }) Assert.DoesNotContain(secret, wire, StringComparison.OrdinalIgnoreCase);
    }

    // ---- AC 7 ----
    [Fact] public async Task MonthBoundariesAreEachClinicsLocalMidnight()
    {
        var salvador = await Clinic("sv"); var madrid = await Clinic("mad", "Europe/Madrid");
        var sv = TimeSpan.FromHours(-6); var cest = TimeSpan.FromHours(2); // Madrid is on summer time from 2026-03-29
        scope.Id = salvador.Id;
        Message(salvador.Conversations[0], "patient", new(2026, 3, 31, 23, 30, 0, sv), "received");  // 05:30 UTC on April 1st: still March here
        Message(salvador.Conversations[0], "patient", new(2026, 4, 1, 0, 30, 0, sv), "received");    // April here
        await db.SaveChangesAsync(); scope.Id = madrid.Id;
        Message(madrid.Conversations[0], "patient", new(2026, 4, 1, 0, 30, 0, cest), "received");    // 22:30 UTC on March 31st: April in Madrid
        Message(madrid.Conversations[0], "patient", new(2026, 3, 1, 0, 30, 0, TimeSpan.FromHours(1)), "received"); // March in Madrid, February in UTC
        await db.SaveChangesAsync(); scope.Id = Guid.Empty;

        int Patients(MonthlyUsage report, Guid id) => report.Tenants.Single(x => x.Id == id).Usage.PatientMessages;
        var march = await UsageReport.Read("2026-03", Owner, db, scope, CancellationToken.None);
        var april = await UsageReport.Read("2026-04", Owner, db, scope, CancellationToken.None);

        Assert.Equal((1, 1), (Patients(march, salvador.Id), Patients(april, salvador.Id)));
        Assert.Equal((1, 1), (Patients(march, madrid.Id), Patients(april, madrid.Id)));
    }

    // ---- AC 23 ----
    [Fact] public async Task AiColumnsAreTheMonthsMeteredSums()
    {
        var a = await Clinic("ia");
        Model(a.Id, new(2026, 3, 1), "nvidia", "nemotron", 2, 100, 50, 10); Model(a.Id, new(2026, 3, 31), "anthropic", "claude-opus-5-5", 1, 7, 3, 0);
        Model(a.Id, new(2026, 2, 28), "nvidia", "nemotron", 50, 5000, 500, 0); Model(a.Id, new(2026, 4, 1), "nvidia", "nemotron", 60, 6000, 600, 0); // the months around
        await db.SaveChangesAsync();
        var b = await Clinic("ib"); Model(b.Id, new(2026, 3, 15), "nvidia", "nemotron", 5, 900, 90, 9); await db.SaveChangesAsync();
        var c = await Clinic("ic"); scope.Id = Guid.Empty;

        var report = await UsageReport.Read("2026-03", Owner, db, scope, CancellationToken.None);

        Assert.True(report.AiMetered);
        UsageCounts Of(Guid id) => report.Tenants.Single(x => x.Id == id).Usage;
        Assert.Equal((3L, 107L, 53L, 10L), (Of(a.Id).AiCalls, Of(a.Id).InputTokens, Of(a.Id).OutputTokens, Of(a.Id).CachedInputTokens));
        Assert.Equal((5L, 900L, 90L, 9L), (Of(b.Id).AiCalls, Of(b.Id).InputTokens, Of(b.Id).OutputTokens, Of(b.Id).CachedInputTokens));
        Assert.Equal((0L, 0L, 0L, 0L), (Of(c.Id).AiCalls, Of(c.Id).InputTokens, Of(c.Id).OutputTokens, Of(c.Id).CachedInputTokens));
    }
    void Model(Guid tenant, DateOnly day, string provider, string model, long calls, long input, long output, long cached)
        => db.AiUsage.Add(new AiUsage { TenantId = tenant, Day = day, Provider = provider, Model = model, Calls = calls, InputTokens = input, OutputTokens = output, CachedInputTokens = cached });

    // ---- AC 9, 23 ----
    [Fact] public async Task TotalsAreTheSumOfTheRows()
    {
        var a = await Clinic("ta"); Message(a.Conversations[0], "patient", March, "received"); Message(a.Conversations[0], "agent", March); Step(a.Conversations[0], "appointment", March);
        Model(a.Id, new(2026, 3, 10), "nvidia", "nemotron", 4, 400, 40, 4); await db.SaveChangesAsync();
        var b = await Clinic("tb"); Message(b.Conversations[0], "human", March); await db.SaveChangesAsync();
        scope.Id = Guid.Empty;

        var report = await UsageReport.Read("2026-03", Owner, db, scope, CancellationToken.None);

        var rows = report.Tenants.Select(x => x.Usage).ToList();
        Assert.True(rows.Count >= 2);
        Assert.Equal(new UsageCounts(rows.Sum(x => x.NumbersConnected), rows.Sum(x => x.NumbersEnabled), rows.Sum(x => x.PatientMessages), rows.Sum(x => x.AgentReplies), rows.Sum(x => x.HumanReplies),
            rows.Sum(x => x.TemplatesSent), rows.Sum(x => x.AgentConversations), rows.Sum(x => x.Appointments), rows.Sum(x => x.Registrations), rows.Sum(x => x.Prescriptions),
            rows.Sum(x => x.AiCalls), rows.Sum(x => x.InputTokens), rows.Sum(x => x.OutputTokens), rows.Sum(x => x.CachedInputTokens)), report.Totals);
        Assert.True(report.Totals.AiCalls >= 4);
    }

    // ---- AC 12 ----
    [Fact] public async Task EveryReadIsAuditedInEachClinicItShows()
    {
        var a = await Clinic("aa"); var b = await Clinic("ab"); scope.Id = Guid.Empty;

        var report = await UsageReport.Read("2026-03", Owner, db, scope, CancellationToken.None);

        var audits = await db.Audits.IgnoreQueryFilters().Where(x => x.Actor == "platform:" + sub).ToListAsync();
        Assert.All(new[] { a.Id, b.Id }, id => Assert.Single(audits, x => x.TenantId == id && x.Action == "usage.read" && x.Resource == "2026-03"));
        Assert.Equal(report.Tenants.Select(x => x.Id).Order(), audits.Select(x => x.TenantId).Order());
    }

    // ---- AC 13 ----
    [Theory][InlineData(null)][InlineData("")][InlineData("2026-3")][InlineData("2026-13")][InlineData("2026-03-01")][InlineData("marzo")]
    public async Task AMalformedMonthIsABadRequest(string? month)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => UsageReport.Read(month, Owner, db, scope, CancellationToken.None));
        Assert.False(await db.Audits.IgnoreQueryFilters().AnyAsync(x => x.Actor == "platform:" + sub));
    }

    // ---- AC 2 ----
    [Theory][InlineData("admin")][InlineData("agent")][InlineData("doctor")][InlineData("")]
    public async Task OnlyThePlatformOwnerReadsUsage(string role)
        // db is null on purpose: the refusal comes before any query.
        => await Assert.ThrowsAsync<AccessDeniedException>(() => UsageReport.Read("2026-03", new CurrentUser { Role = role, Subject = "hospital-user" }, null!, new TenantScope(), CancellationToken.None));

    // ---- AC 3 ----
    [Fact] public async Task AHospitalTokenWithTheActingHeaderIsRefused()
    {
        var endpoint = new Endpoint(null, new EndpointMetadataCollection(new PlatformOperable(NeedsTenant: false)), "GET /api/platform/usage");
        var hospital = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "hospital-user"), new Claim("tenant_id", Guid.NewGuid().ToString()), new Claim("realm_access", "{\"roles\":[\"Administrador\"]}")], "test")) };
        hospital.Request.Headers[PlatformOwner.Header] = Guid.NewGuid().ToString(); hospital.SetEndpoint(endpoint);
        Assert.Equal(403, await PlatformOwner.Gate(hospital, null!, new TenantScope(), new CurrentUser()));
        // Without the header a hospital token falls through to Bind and then to the handler, which refuses it (AC 2).
        hospital.Request.Headers.Remove(PlatformOwner.Header);
        Assert.Null(await PlatformOwner.Gate(hospital, null!, new TenantScope(), new CurrentUser()));
        // The owner reaches it without choosing a reception: nothing is acted on.
        var owner = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", sub), new Claim("realm_access", "{\"roles\":[\"platform-owner\"]}")], "test")) };
        owner.SetEndpoint(endpoint); var acted = new TenantScope(); var user = new CurrentUser();
        Assert.Equal(0, await PlatformOwner.Gate(owner, null!, acted, user));
        Assert.Equal((Guid.Empty, "platform"), (acted.Id, user.Role));
    }
}
