using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Recepcion;
using Xunit;
// MR-39: el barrido diario que corre AgentWorker.RunTenant, no una copia.
public sealed class RetentionTests : IAsyncLifetime
{
    readonly AgentHarness h = new(), other = new();
    public async Task InitializeAsync() { await h.Start(); await other.Start(); }
    public async Task DisposeAsync() { await h.DisposeAsync(); await other.DisposeAsync(); }

    static async Task<Job> Job(AgentHarness at, string status, int days)
    {
        var j = new Job { TenantId = at.Scope.Id, ConversationId = at.Conversation.Id, Key = "agent:retention-" + Guid.NewGuid(), Status = status, CreatedAt = DateTimeOffset.UtcNow.AddDays(-days), StartedAt = status == "pending" ? null : DateTimeOffset.UtcNow };
        at.Db.Add(j); await at.Db.SaveChangesAsync(); return j;
    }
    static async Task<Receipt> Receipt(AgentHarness at, int days)
    {
        var r = new Receipt { TenantId = at.Scope.Id, Key = "retention-" + Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow.AddDays(-days) };
        at.Db.Add(r); await at.Db.SaveChangesAsync(); return r;
    }

    [Fact]
    public async Task FinishedJobsAndReceiptsOlderThanThirtyDaysAreSweptOnlyForTheTenant()
    {
        // h.Job is this tick's pending turn: it is claimed and finished before the sweep, so pending is checked where nothing claims it.
        var oldDone = await Job(h, "done", 31); var oldFailed = await Job(h, "failed", 31); var recentDone = await Job(h, "done", 29);
        var uncertain = await Job(h, "uncertain", 31); var running = await Job(h, "running", 31);
        var oldReceipt = await Receipt(h, 31); var recentReceipt = await Receipt(h, 29);
        var otherDone = await Job(other, "done", 31); var otherReceipt = await Receipt(other, 31);

        var sender = h.Sender();
        await AgentWorker.RunTenant(h.Worker(h.Runtime(new AgentHarness.Fake(_ => Task.FromResult(AgentHarness.Reply("Respuesta sintética"))), sender), sender), h.Scope.Id, NullLogger.Instance, CancellationToken.None);

        var jobs = await h.Db.Jobs.AsNoTracking().Select(x => x.Id).ToListAsync();
        Assert.DoesNotContain(oldDone.Id, jobs); Assert.DoesNotContain(oldFailed.Id, jobs);
        Assert.Contains(recentDone.Id, jobs); Assert.Contains(uncertain.Id, jobs); Assert.Contains(running.Id, jobs); Assert.Contains(h.Job.Id, jobs);
        var receipts = await h.Db.Receipts.AsNoTracking().Select(x => x.Id).ToListAsync();
        Assert.DoesNotContain(oldReceipt.Id, receipts); Assert.Contains(recentReceipt.Id, receipts);
        Assert.True(await other.Db.Jobs.AsNoTracking().AnyAsync(x => x.Id == otherDone.Id));
        Assert.True(await other.Db.Receipts.AsNoTracking().AnyAsync(x => x.Id == otherReceipt.Id));
    }

    /// <summary>Fails every DELETE, the way a sweep past CommandTimeout would.</summary>
    sealed class FailingDeletes : DbCommandInterceptor
    {
        public int Calls;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        { if (!command.CommandText.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)) return ValueTask.FromResult(result); Calls++; throw new TimeoutException("Synthetic sweep timeout"); }
    }
    sealed class Notes : Microsoft.Extensions.Logging.ILogger
    {
        public readonly List<string> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null; public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format) => Lines.Add(format(state, ex));
    }

    [Fact]
    public async Task ASweepThatFailsNeverHoldsTheTurnBackAndWaitsForTomorrow()
    {
        var old = await Job(h, "done", 31); var fails = new FailingDeletes(); var log = new Notes(); var sender = h.Sender();
        await using var worker = h.With(fails);
        var tenant = h.Worker(h.Runtime(new AgentHarness.Fake(_ => Task.FromResult(AgentHarness.Reply("Respuesta sintética"))), sender), sender, worker);
        await AgentWorker.RunTenant(tenant, h.Scope.Id, log, CancellationToken.None);
        Assert.Equal("done", await h.Db.Jobs.AsNoTracking().Where(x => x.Id == h.Job.Id).Select(x => x.Status).SingleAsync());
        Assert.Equal(1, fails.Calls); Assert.Contains("Retention sweep failed TimeoutException. Tenant " + h.Scope.Id + ".", log.Lines);
        Assert.True(await h.Db.Jobs.AsNoTracking().AnyAsync(x => x.Id == old.Id));
        await AgentWorker.RunTenant(tenant, h.Scope.Id, log, CancellationToken.None);
        Assert.Equal(1, fails.Calls); // a failed day is not retried on every tick
    }

    [Fact]
    public async Task TheSweepDeletesInBatchesUpToTheDailyCap()
    {
        for (var i = 0; i < 5; i++) { await Job(h, "done", 31); await Receipt(h, 31); }
        var pending = await Job(h, "pending", 31);
        await Retention.Daily(h.Db, h.Scope.Id, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None, batch: 2, cap: 4);
        Assert.Equal(1, await h.Db.Jobs.AsNoTracking().CountAsync(x => x.Status == "done"));
        Assert.True(await h.Db.Jobs.AsNoTracking().AnyAsync(x => x.Id == pending.Id)); Assert.True(await h.Db.Jobs.AsNoTracking().AnyAsync(x => x.Id == h.Job.Id));
        Assert.Equal(1, await h.Db.Receipts.AsNoTracking().CountAsync(x => x.CreatedAt < DateTimeOffset.UtcNow.AddDays(-30)));
    }
}
