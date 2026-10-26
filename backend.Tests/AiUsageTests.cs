using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>docs/usage-report.md parte B, AC 17–22: each model call a provider answered is counted for its clinic. The model is simulated; the database and the runtime are real.</summary>
public sealed class AiUsageTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();

    /// <summary>An OpenAI-shaped answer with its usage, as NIM and OpenAI send it; <paramref name="model"/> null: the answer names no model.</summary>
    static HttpResponseMessage Metered(HttpResponseMessage reply, int input, int output, int cached, string? model = "nemotron-synthetic")
    {
        var json = JsonNode.Parse(reply.Content.ReadAsStringAsync().Result)!.AsObject();
        if (model is not null) json["model"] = model;
        json["usage"] = new JsonObject { ["prompt_tokens"] = input, ["completion_tokens"] = output, ["total_tokens"] = input + output, ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cached } };
        return AgentHarness.Json(json);
    }
    static HttpResponseMessage Down() => new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
    static DateOnly Today(string zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(zone)).DateTime);

    // ---- AC 17, 18 ----
    [Fact]
    public async Task EachAnsweredCallAddsToTheClinicsRow()
    {
        // A tool call and then the answer: two model calls in one turn, one row.
        var model = h.Model(Metered(AgentHarness.ToolCall("record_note", new { note = "Nota sintética" }), 1000, 40, 600), Metered(AgentHarness.Reply("Listo, ya lo anoté."), 1100, 20, 1000));

        await h.Runtime(model, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal(2, model.Calls);
        var row = Assert.Single(await h.Db.AiUsage.ToListAsync());
        Assert.Equal((h.Scope.Id, Today("America/El_Salvador"), "nvidia", "nemotron-synthetic", 2L, 2100L, 60L, 1600L),
            (row.TenantId, row.Day, row.Provider, row.Model, row.Calls, row.InputTokens, row.OutputTokens, row.CachedInputTokens));
    }

    // ---- AC 19 ----
    [Fact]
    public async Task OnlyTheProviderThatAnsweredCounts()
    {
        var model = new AgentHarness.Fake(request => Task.FromResult(request.RequestUri!.Host == "api.openai.com" ? Metered(AgentHarness.Reply("Respuesta de respaldo"), 300, 30, 0, model: null) : Down()));

        await h.Runtime(model, h.Sender(), extra: new() { ["OPENAI_API_KEY"] = "synthetic-openai", ["OPENAI_MODEL"] = "synthetic-fallback" }).Run(h.Job, CancellationToken.None);

        Assert.Contains("Respuesta de respaldo", h.Sent);
        var row = Assert.Single(await h.Db.AiUsage.ToListAsync()); // NIM failed: nothing for it
        Assert.Equal(("openai", "synthetic-fallback", 1L, 300L, 30L, 0L), (row.Provider, row.Model, row.Calls, row.InputTokens, row.OutputTokens, row.CachedInputTokens));
    }

    // ---- AC 20 ----
    [Fact]
    public async Task AResponseWithoutUsageCountsTheCallOnly()
    {
        await h.Runtime(h.Model(AgentHarness.Reply("Respuesta sin uso")), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Contains("Respuesta sin uso", h.Sent);
        var row = Assert.Single(await h.Db.AiUsage.ToListAsync());
        Assert.Equal(("nvidia", "synthetic", 1L, 0L, 0L, 0L), (row.Provider, row.Model, row.Calls, row.InputTokens, row.OutputTokens, row.CachedInputTokens));
    }

    // ---- AC 21, 22 ----
    [Fact]
    public async Task TwoClinicsNeverMixAndEachDayIsLocal()
    {
        // UTC+14 and UTC−11 are 25 hours apart: their local dates always differ, so a UTC day would be wrong for at least one.
        await using var other = new AgentHarness(); await other.Start();
        async Task Turn(AgentHarness clinic, string zone, int input)
        {
            (await clinic.Db.Tenants.SingleAsync(x => x.Id == clinic.Scope.Id)).TimeZone = zone; await clinic.Db.SaveChangesAsync();
            await clinic.Runtime(clinic.Model(Metered(AgentHarness.Reply("Hola"), input, 1, 0)), clinic.Sender()).Run(clinic.Job, CancellationToken.None);
        }
        await Turn(h, "Pacific/Kiritimati", 111); await Turn(other, "Pacific/Pago_Pago", 222);

        var mine = Assert.Single(await h.Db.AiUsage.ToListAsync()); var theirs = Assert.Single(await other.Db.AiUsage.ToListAsync());
        Assert.Equal((h.Scope.Id, 111L, Today("Pacific/Kiritimati")), (mine.TenantId, mine.InputTokens, mine.Day));
        Assert.Equal((other.Scope.Id, 222L, Today("Pacific/Pago_Pago")), (theirs.TenantId, theirs.InputTokens, theirs.Day));
        Assert.Equal(2, await h.Db.AiUsage.IgnoreQueryFilters().CountAsync(x => x.TenantId == h.Scope.Id || x.TenantId == other.Scope.Id));
    }
}
