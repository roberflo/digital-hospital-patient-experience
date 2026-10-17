using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Recepcion;
using Recepcion.Integrations;
using Xunit;
// docs/agent-jobs-health.md, criterios 1-6: el barrido y el reclamo que ejecuta AgentWorker, no una copia.
public sealed partial class AgentIntegrationTests {
    static Fake Never()=>new(_=>throw new Exception("The sweep must neither call the model nor send"));
    CrmDb Other()=>new(options,scope,protection);
    ConversationService ServiceOn(CrmDb other)=>new(other,scope,new KapsoClient(new HttpClient(Never()),config));
    async Task Abandon(Job row,int minutes=6){row.Status="running";row.StartedAt=DateTimeOffset.UtcNow.AddMinutes(-minutes);await db.SaveChangesAsync();}
    async Task<List<Activity>> Trail(CrmDb fresh)=>await fresh.Activities.Where(x=>x.ConversationId==conversation.Id&&x.Kind=="handoff").ToListAsync();

    [Fact]public async Task AbandonedTurnLeavesATrailOnceAndNeverRuns(){
        await Abandon(job);var ai=Never();var k=Never();
        var sp=new ServiceCollection().AddSingleton(scope).AddSingleton(db).AddSingleton(Service(k)).AddSingleton(Runtime(ai,k)).BuildServiceProvider();
        await AgentWorker.RunTenant(sp,scope.Id,NullLogger.Instance,CancellationToken.None);
        long revision;DateTimeOffset? started;
        await using(var fresh=Other()){
            var a=Assert.Single(await Trail(fresh));Assert.Equal("Sistema",a.Actor);Assert.Equal("system",a.ActorRole);Assert.Equal(AgentWorker.AbandonedNote,a.Body);Assert.Equal(contact.Id,a.ContactId);
            var c=await fresh.Conversations.SingleAsync(x=>x.Id==conversation.Id);Assert.Equal("human",c.Status);Assert.Equal(AgentWorker.AbandonedNote,c.Summary);revision=c.Revision;
            var j=await fresh.Jobs.SingleAsync(x=>x.Id==job.Id);Assert.Equal("uncertain",j.Status);started=j.StartedAt;
        }
        await AgentWorker.RunTenant(sp,scope.Id,NullLogger.Instance,CancellationToken.None);
        await using(var fresh=Other()){
            Assert.Single(await Trail(fresh));Assert.Equal(revision,(await fresh.Conversations.SingleAsync(x=>x.Id==conversation.Id)).Revision);
            // T5: un turno retirado no vuelve a pending ni a running (INV-1).
            var j=await fresh.Jobs.SingleAsync(x=>x.Id==job.Id);Assert.Equal("uncertain",j.Status);Assert.Equal(started,j.StartedAt);
        }
        Assert.Equal(0,ai.Calls);Assert.Equal(0,k.Calls);
    }
    [Fact]public async Task AbandonedTurnAlreadyWithAPersonKeepsItsSummary(){
        // agent-jobs-health.plan.md §0: pregunta abierta con el propietario; hasta entonces el resumen de quien ya atiende no se pisa (opción B).
        conversation.Status="human";conversation.Summary="Motivo sintético de urgencia";await Abandon(job);
        await AgentWorker.RetireAbandoned(db,scope,Service(Never()),CancellationToken.None);
        await using var fresh=Other();
        Assert.Equal(AgentWorker.AbandonedNote,Assert.Single(await Trail(fresh)).Body);
        var c=await fresh.Conversations.SingleAsync(x=>x.Id==conversation.Id);Assert.Equal("Motivo sintético de urgencia",c.Summary);Assert.Equal("human",c.Status);
        Assert.Equal("uncertain",(await fresh.Jobs.SingleAsync(x=>x.Id==job.Id)).Status);
    }
    [Fact]public async Task SweepLeavesPendingAndRecentRunningAlone(){
        var channel=new Channel{TenantId=scope.Id,Name="Another synthetic number",PhoneNumberId="sweep-"+Guid.NewGuid(),Enabled=true};db.Add(channel);
        var second=new Conversation{TenantId=scope.Id,ContactId=contact.Id,ChannelId=channel.Id,Status="agent"};db.Add(second);
        var recent=new Job{TenantId=scope.Id,ConversationId=second.Id,Key="agent:recent-"+scope.Id};db.Add(recent);await Abandon(recent,1);
        await AgentWorker.RetireAbandoned(db,scope,Service(Never()),CancellationToken.None);
        await using var fresh=Other();
        Assert.Equal("pending",(await fresh.Jobs.SingleAsync(x=>x.Id==job.Id)).Status);Assert.Equal("running",(await fresh.Jobs.SingleAsync(x=>x.Id==recent.Id)).Status);
        Assert.All(await fresh.Conversations.ToListAsync(),c=>{Assert.Equal("agent",c.Status);Assert.Equal(0L,c.Revision);Assert.Equal("",c.Summary);});
        Assert.Equal(2,await fresh.Conversations.CountAsync());Assert.Empty(await fresh.Activities.ToListAsync());
    }
    [Fact]public async Task ConcurrentSweepsRetireOnce(){
        await Abandon(job);
        // El candado es de sesión sobre la conexión de cada CrmDb: cada barrido lleva su propia pareja contexto-servicio.
        await using var first=Other();await using var second=Other();await using var gate=Other();
        var held=await ServiceOn(gate).Lock(conversation.Id);
        var sweeps=Task.WhenAll(AgentWorker.RetireAbandoned(first,scope,ServiceOn(first),CancellationToken.None),AgentWorker.RetireAbandoned(second,scope,ServiceOn(second),CancellationToken.None));
        await Task.Delay(300);held.Dispose();await sweeps;
        await using var fresh=Other();
        Assert.Single(await Trail(fresh));Assert.Equal(1L,(await fresh.Conversations.SingleAsync(x=>x.Id==conversation.Id)).Revision);
        Assert.Equal("uncertain",(await fresh.Jobs.SingleAsync(x=>x.Id==job.Id)).Status);
    }
    [Fact]public async Task ClaimIsWonByOneWorker(){
        await using var other=Other();
        var won=await Task.WhenAll(AgentWorker.Claim(db,job.Id,CancellationToken.None),AgentWorker.Claim(other,job.Id,CancellationToken.None));
        Assert.Equal(1,won.Sum());Assert.Equal("running",(await other.Jobs.SingleAsync(x=>x.Id==job.Id)).Status);
    }
    // El camino reclamo -> Run -> done/failed -> guardado, a través de RunTenant y leído desde otro contexto.
    ServiceProvider Tenant(Fake ai,Fake k)=>new ServiceCollection().AddSingleton(scope).AddSingleton(db).AddSingleton(Service(k)).AddSingleton(Runtime(ai,k)).BuildServiceProvider();
    [Fact]public async Task ClaimedTurnThatRepliesIsSavedAsDone(){
        var ai=new Fake(_=>Task.FromResult(Reply("Respuesta sintética")));var k=Sending();
        await AgentWorker.RunTenant(Tenant(ai,k),scope.Id,NullLogger.Instance,CancellationToken.None);
        await using var fresh=Other();
        var j=await fresh.Jobs.SingleAsync(x=>x.Id==job.Id);Assert.Equal("done",j.Status);Assert.NotNull(j.StartedAt);
        Assert.Equal(1,k.Calls);Assert.Equal("sent",Assert.Single(await fresh.Messages.Where(x=>x.Sender=="agent").ToListAsync()).Status);
        Assert.Empty(await fresh.Activities.Where(x=>x.Kind=="error").ToListAsync());
    }
    [Fact]public async Task ClaimedTurnThatFailsLeavesAnErrorTrail(){
        (await db.Tenants.SingleAsync(x=>x.Id==scope.Id)).EmergencyPhone="2222-0000";await db.SaveChangesAsync();
        var ai=new Fake(_=>throw new InvalidOperationException("Synthetic model failure"));var k=Sending();var tenant=Tenant(ai,k);
        await AgentWorker.RunTenant(tenant,scope.Id,NullLogger.Instance,CancellationToken.None);
        await using var fresh=Other();
        var j=await fresh.Jobs.SingleAsync(x=>x.Id==job.Id);Assert.Equal("failed",j.Status);Assert.Equal("Revisión humana requerida: InvalidOperationException",j.Error);
        var a=Assert.Single(await fresh.Activities.Where(x=>x.ConversationId==conversation.Id&&x.Kind=="error").ToListAsync());Assert.Equal("system",a.ActorRole);Assert.DoesNotContain("Synthetic",a.Body);
        var c=await fresh.Conversations.SingleAsync(x=>x.Id==conversation.Id);Assert.Equal("human",c.Status);Assert.Equal(2L,c.Revision); // the failure, then the message to the patient
        // MR-3: the patient is told once that the team was told, with the hospital's emergency number.
        var told=Assert.Single(await fresh.Messages.Where(x=>x.Sender=="agent").ToListAsync());Assert.Equal("failed:"+job.Id,told.RequestKey);Assert.Equal("sent",told.Status);
        Assert.Equal("No pude completar tu solicitud. Ya avisé al equipo del hospital para que te responda por aquí.\n\nSi es una emergencia, llama al *2222-0000*.",told.Body);Assert.Equal(1,k.Calls);
        await AgentWorker.RunTenant(tenant,scope.Id,NullLogger.Instance,CancellationToken.None);
        Assert.Equal(1,k.Calls);Assert.Single(await fresh.Messages.Where(x=>x.Sender=="agent").ToListAsync());
    }
    [Fact]public async Task AFailedTurnOutsideTheWhatsAppWindowSendsNothing(){
        // Send itself refuses a closed window and the refusal would be swallowed: what proves the check is that nothing was even attempted.
        conversation.LastInboundAt=DateTimeOffset.UtcNow.AddDays(-2);await db.SaveChangesAsync();var log=new Notes();
        var k=Sending();await AgentWorker.RunTenant(Tenant(new Fake(_=>throw new InvalidOperationException("Synthetic model failure")),k),scope.Id,log,CancellationToken.None);
        await using var fresh=Other();Assert.Equal("failed",(await fresh.Jobs.SingleAsync(x=>x.Id==job.Id)).Status);Assert.Equal(0,k.Calls);
        Assert.Empty(await fresh.Messages.Where(x=>x.Sender=="agent").ToListAsync());Assert.DoesNotContain(log.Lines,l=>l.StartsWith("Agent failure notice not sent"));
    }
    [Fact]public async Task AFailedTurnWithoutAnEmergencyNumberSaysOnlyThatTheTeamWasTold(){
        var k=Sending();await AgentWorker.RunTenant(Tenant(new Fake(_=>throw new InvalidOperationException("Synthetic model failure")),k),scope.Id,NullLogger.Instance,CancellationToken.None);
        await using var fresh=Other();
        Assert.Equal("No pude completar tu solicitud. Ya avisé al equipo del hospital para que te responda por aquí.",Assert.Single(await fresh.Messages.Where(x=>x.Sender=="agent").ToListAsync()).Body);Assert.Equal(1,k.Calls);
    }
    [Fact]public async Task AFailedTurnTakenByAPersonMeanwhileSendsNothing(){
        // INV-5: once a person has the conversation, only that person writes to the patient.
        var ai=new Fake(async _=>{await using var other=Other();await other.Conversations.Where(x=>x.Id==conversation.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.Status,"human").SetProperty(c=>c.AssignedTo,"staff-synthetic"));throw new InvalidOperationException("Synthetic model failure");});
        var k=Sending();await AgentWorker.RunTenant(Tenant(ai,k),scope.Id,NullLogger.Instance,CancellationToken.None);
        await using var fresh=Other();Assert.Equal("failed",(await fresh.Jobs.SingleAsync(x=>x.Id==job.Id)).Status);Assert.Equal(0,k.Calls);Assert.Empty(await fresh.Messages.Where(x=>x.Sender=="agent").ToListAsync());
    }
    [Fact]public async Task AFailedTurnAfterTheAgentWasTurnedOffSendsNothing(){
        var ai=new Fake(async _=>{await using var other=Other();await other.Tenants.Where(x=>x.Id==scope.Id).ExecuteUpdateAsync(x=>x.SetProperty(t=>t.AgentEnabled,false));throw new InvalidOperationException("Synthetic model failure");});
        var k=Sending();await AgentWorker.RunTenant(Tenant(ai,k),scope.Id,NullLogger.Instance,CancellationToken.None);
        await using var fresh=Other();Assert.Equal("failed",(await fresh.Jobs.SingleAsync(x=>x.Id==job.Id)).Status);Assert.Equal(0,k.Calls);Assert.Empty(await fresh.Messages.Where(x=>x.Sender=="agent").ToListAsync());
    }
    sealed class Notes:Microsoft.Extensions.Logging.ILogger{
        public readonly List<string> Lines=[];
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level)=>true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level,Microsoft.Extensions.Logging.EventId id,TState state,Exception? ex,Func<TState,Exception?,string> format)=>Lines.Add(format(state,ex));
    }
    [Fact]public async Task AHungTenantDoesNotHoldBackAnother(){
        // MR-4: tenant A's model never answers; tenant B's turn, queued once A is stuck, is still served by the same running worker.
        job.Status="done";var hung="Colgado-"+scope.Id;var inA=new Message{TenantId=scope.Id,ConversationId=conversation.Id,ExternalId="hung-"+scope.Id,Body=hung,Sender="patient"};db.Add(inA);
        var jobA=new Job{TenantId=scope.Id,ConversationId=conversation.Id,Key="agent:"+inA.ExternalId};db.Add(jobA);await db.SaveChangesAsync();
        var other=new TenantScope{Id=Guid.NewGuid()};await using var b=new CrmDb(options,other,protection);
        b.Tenants.Add(new Tenant{Id=other.Id,Name="Synthetic tenant B",AgentEnabled=true});var contactB=new Contact{TenantId=other.Id,Name="Synthetic",Phone="50370000001",PhoneHash=Guid.NewGuid().ToString()};
        var channelB=new Channel{TenantId=other.Id,Name="Synthetic B",PhoneNumberId=Guid.NewGuid().ToString("N"),Enabled=true};b.Add(contactB);b.Add(channelB);
        var conversationB=new Conversation{TenantId=other.Id,ChannelId=channelB.Id,ContactId=contactB.Id,Status="agent",LastInboundAt=DateTimeOffset.UtcNow};b.Add(conversationB);
        var inB=new Message{TenantId=other.Id,ConversationId=conversationB.Id,ExternalId="in-"+other.Id,Body="Consulta sintética B",Sender="patient"};b.Add(inB);await b.SaveChangesAsync();
        var stuck=new TaskCompletionSource();
        var model=AgentHarness.Hang(req=>{if(!req.Content!.ReadAsStringAsync().Result.Contains(hung))return Reply("Respuesta sintética");stuck.TrySetResult();return null;});
        var wire=Sending();var nowhere=new Fake(_=>Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)));
        var services=new ServiceCollection().AddScoped<TenantScope>().AddScoped(sp=>new CrmDb(options,sp.GetRequiredService<TenantScope>(),protection))
            .AddScoped(sp=>new ConversationService(sp.GetRequiredService<CrmDb>(),sp.GetRequiredService<TenantScope>(),new KapsoClient(new HttpClient(wire),config)))
            .AddScoped(sp=>new AgentRuntime(new HttpClient(model),config,sp.GetRequiredService<CrmDb>(),sp.GetRequiredService<TenantScope>(),new HospitalClient(new HttpClient(nowhere),config),sp.GetRequiredService<ConversationService>(),new KapsoClient(new HttpClient(wire),config)));
        await using var provider=services.BuildServiceProvider();
        using var worker=new AgentWorker(provider.GetRequiredService<IServiceScopeFactory>(),NullLogger<AgentWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);string statusA="",statusB="";
        try{
            await stuck.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var jobB=new Job{TenantId=other.Id,ConversationId=conversationB.Id,Key="agent:"+inB.ExternalId};b.Add(jobB);await b.SaveChangesAsync();
            for(var i=0;i<300&&statusB!="done";i++){await Task.Delay(100);await using var fresh=new CrmDb(options,other,protection);statusB=await fresh.Jobs.Where(x=>x.Id==jobB.Id).Select(x=>x.Status).SingleAsync();}
            await using(var fresh=Other())statusA=await fresh.Jobs.Where(x=>x.Id==jobA.Id).Select(x=>x.Status).SingleAsync();
        }
        finally{await worker.StopAsync(CancellationToken.None);}
        Assert.Equal("done",statusB);Assert.Equal("running",statusA);
    }
    [Fact]public async Task AbandonedTurnIsRetiredByTheRunningWorker(){
        await Abandon(job);
        // El worker real recorre todos los inquilinos de la base de pruebas: con el envío apagado Run vuelve sin llamar a nada, y cualquier transporte lanzaría.
        var off=new ConfigurationBuilder().AddConfiguration(config).AddInMemoryCollection(new Dictionary<string,string?>{{"SEND_ENABLED","false"}}).Build();var wire=Never();
        var services=new ServiceCollection().AddScoped<TenantScope>().AddScoped(sp=>new CrmDb(options,sp.GetRequiredService<TenantScope>(),protection))
            .AddScoped(sp=>new ConversationService(sp.GetRequiredService<CrmDb>(),sp.GetRequiredService<TenantScope>(),new KapsoClient(new HttpClient(wire),off)))
            .AddScoped(sp=>new AgentRuntime(new HttpClient(wire),off,sp.GetRequiredService<CrmDb>(),sp.GetRequiredService<TenantScope>(),new HospitalClient(new HttpClient(wire),off),sp.GetRequiredService<ConversationService>(),new KapsoClient(new HttpClient(wire),off)));
        await using var provider=services.BuildServiceProvider();
        using var worker=new AgentWorker(provider.GetRequiredService<IServiceScopeFactory>(),NullLogger<AgentWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);var status="";
        try{for(var i=0;i<100&&status!="uncertain";i++){await Task.Delay(100);await using var fresh=Other();status=await fresh.Jobs.Where(x=>x.Id==job.Id).Select(x=>x.Status).SingleAsync();}}
        finally{await worker.StopAsync(CancellationToken.None);}
        Assert.Equal("uncertain",status);Assert.Equal(0,wire.Calls);
        await using var after=Other();Assert.Equal(AgentWorker.AbandonedNote,Assert.Single(await Trail(after)).Body);
    }
}
