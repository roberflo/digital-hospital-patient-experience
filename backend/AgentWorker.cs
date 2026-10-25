using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

public sealed class AgentWorker(IServiceScopeFactory scopes, ILogger<AgentWorker> log) : BackgroundService
{
    public const int AbandonedAfterMinutes = 5;
    public const string AbandonedNote = "Atención automática interrumpida. Revisa el historial antes de responder.";
    // ponytail: TurnsAtOnce is per process and every tenant is listed each tick; list only tenants with work if hundreds of hospitals make that cost.
    public const int TurnsPerTenant = 3, TurnsAtOnce = 8;
    public static readonly TimeSpan TurnDeadline = TimeSpan.FromMinutes(2);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Up to three turns of one tenant at once, on different conversations, and eight in all, each in its own scope: ten patients writing
        // at 8:00 do not queue behind one another, and a hung turn holds back only its own slot. Slot 0 also does the tenant's housekeeping.
        var running = new Dictionary<(Guid Tenant, int Slot), Task>();
        void Prune() { foreach (var done in running.Where(x => x.Value.IsCompleted).Select(x => x.Key).ToList()) running.Remove(done); }
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var root = scopes.CreateScope(); var db = root.ServiceProvider.GetRequiredService<CrmDb>();
                    var tenants = await db.Tenants.Select(x => x.Id).ToListAsync(stoppingToken);
                    var pending = await db.Jobs.IgnoreQueryFilters().Where(x => x.Status == "pending").GroupBy(x => x.TenantId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, stoppingToken);
                    foreach (var tid in tenants)
                        for (var slot = 0; slot < Math.Min(TurnsPerTenant, Math.Max(1, pending.GetValueOrDefault(tid))); slot++)
                        {
                            Prune(); if (running.ContainsKey((tid, slot))) continue;
                            while (running.Count >= TurnsAtOnce) { await Task.WhenAny(running.Values).WaitAsync(stoppingToken); Prune(); }
                            var housekeeping = slot == 0;
                            running[(tid, slot)] = Task.Run(() => Turn(tid, housekeeping, stoppingToken), CancellationToken.None);
                        }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Worker unavailable {ErrorType}", ex.GetType().Name); }
                await Task.Delay(1500, stoppingToken);
            }
        }
        finally { await Task.WhenAll(running.Values); }
    }
    async Task Turn(Guid tid, bool housekeeping, CancellationToken stoppingToken)
    {
        try { await using var s = scopes.CreateAsyncScope(); await RunTenant(s.ServiceProvider, tid, log, stoppingToken, housekeeping); }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { log.LogWarning("Worker unavailable {ErrorType}", ex.GetType().Name); }
        catch (OperationCanceledException) { }
    }
    /// <summary>The tenant's turn, then the daily retention sweep: the sweep runs after the claim, and its failure never holds the turn back (MR-39).</summary>
    public static async Task RunTenant(IServiceProvider sp, Guid tid, ILogger log, CancellationToken stoppingToken, bool housekeeping = true)
    {
        await Turn(sp, tid, log, stoppingToken, housekeeping); if (housekeeping) await Retention.Daily(sp.GetRequiredService<CrmDb>(), tid, log, stoppingToken);
    }
    /// <summary>The lock a turn holds from its claim to its end: never two turns of one conversation at once, whatever slot runs them.</summary>
    public static Guid TurnLockId(Guid conversation) => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("agent-turn:" + conversation)).AsSpan(0, 16));
    static async Task Turn(IServiceProvider sp, Guid tid, ILogger log, CancellationToken stoppingToken, bool housekeeping)
    {
        var scope = sp.GetRequiredService<TenantScope>(); scope.Id = tid; var d = sp.GetRequiredService<CrmDb>(); var service = sp.GetRequiredService<ConversationService>();
        if (housekeeping) { await InboxWorkflow.WakeDue(d,scope,service,stoppingToken); await RetireAbandoned(d, scope, service, stoppingToken); }
        var candidates = await d.Jobs.Where(x => x.Status == "pending" && !d.Jobs.Any(r => r.ConversationId == x.ConversationId && r.Status == "running")).OrderBy(x => x.CreatedAt).Take(TurnsPerTenant).ToListAsync(stoppingToken);
        Job? job = null; IDisposable? turn = null;
        foreach (var candidate in candidates)
        {
            if (await ConversationService.TryLock(d, TurnLockId(candidate.ConversationId), stoppingToken) is not { } held) continue;
            try
            {
                // Under the turn lock a running job of this conversation can only be a dead process's, which RetireAbandoned retires.
                if (!await d.Jobs.AnyAsync(r => r.ConversationId == candidate.ConversationId && r.Status == "running", stoppingToken) && await Claim(d, candidate.Id, stoppingToken) == 1) { (job, turn) = (candidate, held); break; }
            }
            catch { held.Dispose(); throw; }
            held.Dispose();
        }
        if (job is null) return;
        using var lease = turn;
        job.Status = "running"; job.StartedAt = DateTimeOffset.UtcNow; var wasAgent = false;
        try { using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); deadline.CancelAfter(TurnDeadline); await sp.GetRequiredService<AgentRuntime>().Run(job, deadline.Token); job.Status = "done"; }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            job.Status = "failed"; job.Error = "Revisión humana requerida: " + ex.GetType().Name;
            // As it stands now, not as this turn read it: a person may have taken the conversation meanwhile (INV-5).
            wasAgent = await d.Conversations.AsNoTracking().AnyAsync(x => x.Id == job.ConversationId && x.Status == "agent" && x.AssignedTo == null, CancellationToken.None);
            var c = await d.Conversations.SingleAsync(x => x.Id == job.ConversationId, CancellationToken.None); c.Status = "human"; c.Revision++;
            d.Activities.Add(new Activity { TenantId = tid, ConversationId = c.Id, ContactId = c.ContactId, Kind = "error", Actor = "Sistema", ActorRole = "system", Body = "El agente no pudo completar la atención. Revisa historial e integraciones." });
            log.LogWarning("Agent job failed {JobId} {ErrorType} {StackTrace}", job.Id, ex.GetType().Name, ex.StackTrace);
        }
        await d.SaveChangesAsync(CancellationToken.None);
        if (job.Status == "failed" && wasAgent) await TellFailed(d, service, job, log);
    }
    /// <summary>A turn that failed is not left in silence: the patient is told once, by the job's key, while WhatsApp still lets us write.
    /// Only for a conversation the agent still had, of a hospital that still has the agent on.</summary>
    static async Task TellFailed(CrmDb d, ConversationService service, Job job, ILogger log)
    {
        try
        {
            var c = await d.Conversations.AsNoTracking().SingleAsync(x => x.Id == job.ConversationId, CancellationToken.None);
            if (!Rules.WithinWindow(c.LastInboundAt, DateTimeOffset.UtcNow) || !await d.Channels.AnyAsync(x => x.Id == c.ChannelId && x.Enabled, CancellationToken.None)) return;
            var tenant = await d.Tenants.Where(x => x.Id == job.TenantId).Select(x => new { x.AgentEnabled, x.EmergencyPhone }).SingleAsync(CancellationToken.None);
            if (!tenant.AgentEnabled) return; var phone = tenant.EmergencyPhone;
            await service.Send(c.Id, "No pude completar tu solicitud. Ya avisé al equipo del hospital para que te responda por aquí." + (phone is { Length: > 0 } ? $"\n\nSi es una emergencia, llama al *{phone}*." : ""), "agent", "failed:" + job.Id, ct: CancellationToken.None);
        }
        catch (Exception ex) { log.LogWarning("Agent failure notice not sent {JobId} {ErrorType}", job.Id, ex.GetType().Name); } // the failed job and its trail are already saved
    }
    public static async Task RetireAbandoned(CrmDb d, TenantScope scope, ConversationService service, CancellationToken ct)
    {
        // A send whose process died between "sending" and Kapso's answer: it may or may not have gone out, so it is uncertain, never retried.
        // The conversation goes to a person with the same trail Send leaves, so the inbox shows it.
        var sendingCutoff = DateTimeOffset.UtcNow.AddMinutes(-AbandonedAfterMinutes);
        foreach (var stuck in await d.Messages.Where(x => x.Status == "sending" && x.ReceivedAt < sendingCutoff).Select(x => new { x.Id, x.ConversationId }).ToListAsync(ct))
        {
            using var lease = await service.Lock(stuck.ConversationId, ct);
            // One SaveChanges: the message never becomes uncertain without its trail and its handoff.
            var message = await d.Messages.SingleAsync(x => x.Id == stuck.Id, ct); await d.Entry(message).ReloadAsync(ct); if (message.Status != "sending") continue;
            message.Status = "uncertain";
            var c = await d.Conversations.SingleAsync(x => x.Id == stuck.ConversationId, ct); await d.Entry(c).ReloadAsync(ct); c.Status = "human"; c.Revision++; c.UpdatedAt = DateTimeOffset.UtcNow;
            d.Activities.Add(new Activity { TenantId = scope.Id, ConversationId = c.Id, ContactId = c.ContactId, MessageId = stuck.Id, Kind = "delivery", Actor = "Sistema", ActorRole = "system", Body = "Entrega sin confirmar. Comprueba WhatsApp antes de volver a enviar." });
            await d.SaveChangesAsync(ct);
        }
        var due = await d.Jobs.Where(x => x.Status == "running" && x.StartedAt < DateTimeOffset.UtcNow.AddMinutes(-AbandonedAfterMinutes)).Select(x => new { x.Id, x.ConversationId }).ToListAsync(ct);
        foreach (var row in due)
        {
            using var lease = await service.Lock(row.ConversationId, ct);
            var stale = await d.Jobs.SingleAsync(x => x.Id == row.Id, ct); await d.Entry(stale).ReloadAsync(ct);
            if (stale.Status != "running" || !(stale.StartedAt < DateTimeOffset.UtcNow.AddMinutes(-AbandonedAfterMinutes))) continue;
            stale.Status = "uncertain"; stale.Error = "Worker interrumpido; requiere conciliación.";
            var c = await d.Conversations.SingleAsync(x => x.Id == stale.ConversationId, ct); await d.Entry(c).ReloadAsync(ct);
            // agent-jobs-health.plan.md §0 (abierta): si ya la atendía una persona, su Summary se conserva.
            if (c.Status != "human") c.Summary = AbandonedNote;
            c.Status = "human"; c.Revision++;
            d.Activities.Add(InboxWorkflow.Event(scope, c, "Sistema", "handoff", AbandonedNote));
            await d.SaveChangesAsync(ct);
        }
    }
    public static Task<int> Claim(CrmDb d, Guid jobId, CancellationToken ct) =>
        d.Jobs.Where(x => x.Id == jobId && x.Status == "pending").ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, "running").SetProperty(j => j.StartedAt, DateTimeOffset.UtcNow), ct);
}
