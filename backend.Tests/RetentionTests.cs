using Microsoft.EntityFrameworkCore;
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
        h.Job.CreatedAt = DateTimeOffset.UtcNow.AddDays(-31); await h.Db.SaveChangesAsync(); // the pending turn, 31 days old
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
}
