using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Recepcion;
using Recepcion.Integrations;
using Xunit;
public sealed class AgentIntegrationTests:IAsyncLifetime {
    CrmDb db=null!;TenantScope scope=new(){Id=Guid.NewGuid()};IConfiguration config=null!;Conversation conversation=null!;Contact contact=null!;Job job=null!;
    DbContextOptions<CrmDb> options=null!;IDataProtectionProvider protection=new EphemeralDataProtectionProvider();
    public async Task InitializeAsync(){
        var connection=Environment.GetEnvironmentVariable("TEST_DATABASE");
        if(string.IsNullOrEmpty(connection))throw new InvalidOperationException("Run scripts/test-backend.sh to provide the isolated PostgreSQL database.");
        options=new DbContextOptionsBuilder<CrmDb>().UseNpgsql(connection).Options;db=new(options,scope,protection);await db.Database.MigrateAsync();
        config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"SEND_ENABLED","true"},{"NVIDIA_API_KEY","synthetic"},{"AI_MODEL","synthetic"},{"KAPSO_API_KEY","synthetic"}}).Build();
        db.Tenants.Add(new Tenant{Id=scope.Id,Name="Synthetic tenant",AgentEnabled=true});await db.SaveChangesAsync();
        contact=new(){TenantId=scope.Id,Name="Synthetic",Phone="50370000000",PhoneHash=Guid.NewGuid().ToString()};var channel=new Channel{TenantId=scope.Id,Name="Synthetic",PhoneNumberId=Guid.NewGuid().ToString("N"),Enabled=true};db.Add(contact);db.Add(channel);
        conversation=new(){TenantId=scope.Id,ChannelId=channel.Id,ContactId=contact.Id,Status="agent",LastInboundAt=DateTimeOffset.UtcNow};db.Add(conversation);
        db.Messages.Add(new Message{TenantId=scope.Id,ConversationId=conversation.Id,ExternalId="in-"+scope.Id,Body="Consulta sintética",Sender="patient"});
        job=new(){TenantId=scope.Id,ConversationId=conversation.Id,Key="agent:in-"+scope.Id};db.Add(job);await db.SaveChangesAsync();
    }
    public async Task DisposeAsync(){if(db!=null)await db.DisposeAsync();}
    [Theory][InlineData("pending")][InlineData("snoozed")][InlineData("resolved")]
    public async Task InactiveWorkflowNeverCallsModel(string state){
        conversation.State=state;await db.SaveChangesAsync();
        var ai=new Fake(_=>throw new Exception("Must not invoke model"));var k=new Fake(_=>throw new Exception("Must not send"));
        await Runtime(ai,k).Run(job,CancellationToken.None);Assert.Equal(0,ai.Calls);Assert.Equal(0,k.Calls);
    }
    [Fact]public async Task ExpiredSnoozeReopensOnceWithoutSending(){
        conversation.State="snoozed";conversation.Status="human";conversation.SnoozedUntil=DateTimeOffset.UtcNow.AddMinutes(-1);await db.SaveChangesAsync();
        var k=new Fake(_=>throw new Exception("Must not send"));
        await InboxWorkflow.WakeDue(db,scope,Service(k),CancellationToken.None);
        await InboxWorkflow.WakeDue(db,scope,Service(k),CancellationToken.None);
        Assert.Equal("open",conversation.State);Assert.Equal("human",conversation.Status);Assert.Null(conversation.SnoozedUntil);
        Assert.Single(await db.Activities.Where(x=>x.ConversationId==conversation.Id&&x.Kind=="conversation_state").ToListAsync());Assert.Equal(0,k.Calls);
    }
    [Fact]public async Task HumanOwnedConversationNeverCallsModel(){conversation.Status="human";await db.SaveChangesAsync();var ai=new Fake(_=>throw new Exception("Must not invoke model"));var k=new Fake(_=>throw new Exception("Must not send"));await Runtime(ai,k).Run(job,CancellationToken.None);Assert.Equal(0,ai.Calls);Assert.Equal(0,k.Calls);}
    [Fact]public async Task KapsoOnboardingRecoversCustomerWithoutDuplicating(){
        var handler=new Fake(req=>{Assert.Equal(HttpMethod.Get,req.Method);Assert.Contains(scope.Id.ToString(),req.RequestUri!.Query);return Task.FromResult(Json(new{data=new[]{new{id="existing-customer",external_customer_id=scope.Id.ToString()}}}));});
        Assert.Equal("existing-customer",await new KapsoClient(new HttpClient(handler),config).EnsureCustomer(scope.Id,"Synthetic hospital"));Assert.Equal(1,handler.Calls);
    }
    [Fact]public async Task KapsoOnboardingCreatesWithTenantExternalId(){
        var handler=new Fake(async req=>{
            if(req.Method==HttpMethod.Get)return Json(new{data=Array.Empty<object>()});
            using var body=JsonDocument.Parse(await req.Content!.ReadAsStringAsync());Assert.Equal(scope.Id.ToString(),body.RootElement.GetProperty("customer").GetProperty("external_customer_id").GetString());
            return Json(new{data=new{id="new-customer"}});
        });
        Assert.Equal("new-customer",await new KapsoClient(new HttpClient(handler),config).EnsureCustomer(scope.Id,"Synthetic hospital"));Assert.Equal(2,handler.Calls);
    }
    [Fact]public async Task NewerInboundCoalescesOlderJob(){db.Add(new Message{TenantId=scope.Id,ConversationId=conversation.Id,ExternalId="new-"+scope.Id,Body="Nueva consulta",CreatedAt=DateTimeOffset.UtcNow.AddSeconds(1)});await db.SaveChangesAsync();var ai=new Fake(_=>throw new Exception());var k=new Fake(_=>throw new Exception());await Runtime(ai,k).Run(job,CancellationToken.None);Assert.Equal(0,ai.Calls);}
    [Fact]public async Task ModelReplyCannotOverrideHumanHandoff(){var ai=new Fake(async _=>{await using var other=new CrmDb(options,scope,protection);await other.Conversations.Where(x=>x.Id==conversation.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"human").SetProperty(x=>x.Revision,1));return Reply("Respuesta que debe descartarse");});var k=new Fake(_=>throw new Exception("Must not send"));await Runtime(ai,k).Run(job,CancellationToken.None);Assert.Equal(0,k.Calls);Assert.Single(await db.Messages.ToListAsync());}
    [Fact]public async Task DisabledTenantSuppressesInFlightReply(){
        var ai=new Fake(async _=>{await using var other=new CrmDb(options,scope,protection);await other.Tenants.Where(x=>x.Id==scope.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.AgentEnabled,false));return Reply("Discard this");});
        var k=new Fake(_=>throw new Exception("Must not send"));await Runtime(ai,k).Run(job,CancellationToken.None);Assert.Equal(0,k.Calls);
    }
    [Fact]public async Task ScopedPatientAgendaUsesBoundedPatientEndpoint(){
        var appointment=Guid.NewGuid();var day=new DateOnly(2026,10,3);
        var handler=new Fake(req=>{
            if(req.RequestUri!.AbsolutePath.StartsWith("/v1/patients/"))return Task.FromResult(Json(new{patientId=contact.Id,phone=contact.Phone}));
            Assert.Equal($"/v1/agenda/patients/{contact.Id}",req.RequestUri.AbsolutePath);
            Assert.Contains("from=2026-10-03&to=2026-10-03",req.RequestUri.Query);
            return Task.FromResult(Json(new{rows=new[]{new{appointmentId=appointment,scheduledStart=DateTimeOffset.Parse("2026-10-03T19:00:00-06:00"),durationMinutes=30,clinicianName="Doctor sintético",status="booked"}}}));
        });
        var cfg=new ConfigurationBuilder().AddConfiguration(GoogleConfig()).AddInMemoryCollection(new Dictionary<string,string?>{[$"Hospital:Tenants:{scope.Id}:UsePatientAgenda"]="true"}).Build();
        var client=new HospitalClient(new HttpClient(handler),cfg);
        var result=await client.GetPatientAppointmentsAsync(scope.Id,contact.Id,contact.Phone,day);
        Assert.Equal(appointment,result[0].GetProperty("appointmentId").GetGuid());
        Assert.False(result[0].TryGetProperty("patientId",out _));
        var before=handler.Calls;
        await Assert.ThrowsAsync<ArgumentException>(()=>client.GetPatientAppointmentRangeAsync(scope.Id,contact.Id,contact.Phone,day,day.AddDays(31)));
        Assert.Equal(before,handler.Calls);
    }
    [Fact]public async Task SlotValidationUsesLocalDayAndRequestedDoctor(){
        var doctor=Guid.NewGuid();var start=DateTimeOffset.UtcNow.AddDays(2).Date.AddHours(1);var instant=new DateTimeOffset(start,TimeSpan.Zero);
        var expected=HospitalClient.ClinicalDay(instant,"America/El_Salvador");
        var handler=new Fake(req=>{
            Assert.Contains($"from={expected:yyyy-MM-dd}&to={expected:yyyy-MM-dd}",req.RequestUri!.Query);
            var slot=new {startsAt=instant,durationMinutes=30,offered=true,takenBy=0};
            var day=new {slots=new[]{slot}};
            return Task.FromResult(Json(new{professionals=new[]{new{clinicianId=Guid.NewGuid(),days=new[]{day}}}}));
        });
        Assert.False(await new HospitalClient(new HttpClient(handler),GoogleConfig()).IsSlotAvailableAsync(scope.Id,doctor,instant,30,"America/El_Salvador"));
    }
    [Fact]public async Task PatientAgendaNeverReturnsOtherPatientsOrIdentifiers(){
        var own=Guid.NewGuid();var other=Guid.NewGuid();
        var handler=new Fake(req=>Task.FromResult(req.RequestUri!.AbsolutePath.StartsWith("/v1/patients/")
            ?Json(new{patientId=contact.Id,phone=contact.Phone})
            :Json(new{rows=new[]{
                new{appointmentId=own,patientId=contact.Id,displayName="PRIVATE NAME",scheduledStart=DateTimeOffset.UtcNow,durationMinutes=30,clinicianName="Doctor",status="booked"},
                new{appointmentId=other,patientId=Guid.NewGuid(),displayName="OTHER PATIENT",scheduledStart=DateTimeOffset.UtcNow,durationMinutes=30,clinicianName="Doctor",status="booked"}
            }})));
        var rows=await new HospitalClient(new HttpClient(handler),GoogleConfig()).GetPatientAppointmentsAsync(scope.Id,contact.Id,contact.Phone,DateOnly.FromDateTime(DateTime.UtcNow));
        Assert.Equal(1,rows.GetArrayLength());Assert.Equal(own,rows[0].GetProperty("appointmentId").GetGuid());
        Assert.DoesNotContain("PRIVATE NAME",rows.GetRawText());Assert.DoesNotContain("OTHER PATIENT",rows.GetRawText());Assert.DoesNotContain(contact.Id.ToString(),rows.GetRawText());Assert.DoesNotContain(other.ToString(),rows.GetRawText());
    }
    [Fact]public async Task SuccessfulReplyCreatesOneMessageAndDeduplicatesSend(){var ai=new Fake(_=>Task.FromResult(Reply("Respuesta sintética")));var k=Sending();await Runtime(ai,k).Run(job,CancellationToken.None);var service=Service(k);var same=await service.Send(conversation.Id,"Respuesta sintética","agent","agent:"+job.Id);Assert.Equal("sent",same.Status);Assert.Equal(1,k.Calls);Assert.Equal(2,await db.Messages.CountAsync());}
    [Fact]public async Task UncertainDeliveryIsNeverRetried(){var k=new Fake(_=>throw new HttpRequestException("Synthetic timeout"));var service=Service(k);var first=await service.Send(conversation.Id,"Texto sintético","human","test-key");var second=await service.Send(conversation.Id,"Texto sintético","human","test-key");Assert.Equal("uncertain",first.Status);Assert.Equal(first.Id,second.Id);Assert.Equal(1,k.Calls);Assert.Equal("human",conversation.Status);await Assert.ThrowsAsync<ArgumentException>(()=>service.Send(conversation.Id,"Otro texto","human","test-key"));}
    [Fact]public async Task HandoffToolPausesAndAcknowledgesPatient(){
        var tool=new { id="call1",type="function",function=new {name="handoff",arguments="{\"reason\":\"Recepción debe ayudar\"}"}};
        var ai=new Fake(_=>Task.FromResult(Json(new {choices=new[]{new {message=new {role="assistant",content=(string?)null,tool_calls=new[]{tool}}}}})));
        var k=Sending();await Runtime(ai,k).Run(job,CancellationToken.None);
        Assert.Equal("human",conversation.Status);Assert.Equal(1,k.Calls);Assert.Contains(await db.Activities.ToListAsync(),x=>x.Kind=="handoff");
    }
    [Fact]public async Task GoogleUsesDeterministicEventsAndExcludesPatientData(){
        var tenant=await db.Tenants.SingleAsync(x=>x.Id==scope.Id);tenant.GoogleRefreshToken="synthetic-refresh";tenant.GoogleCalendarId="synthetic-calendar";await db.SaveChangesAsync();
        var appointment=Guid.NewGuid();var calls=new List<string>();var config=GoogleConfig();
        var hospital=new HospitalClient(new HttpClient(new Fake(_=>Task.FromResult(Json(new{rows=new[]{new{appointmentId=appointment,patientId=contact.Id,displayName="PRIVATE PATIENT NAME",scheduledStart=DateTimeOffset.UtcNow.AddHours(1),durationMinutes=30,status="booked",clinicianName="Doctor Fixture"}}})))),config);
        var google=new Fake(async req=>{
            if(req.RequestUri!.Host=="oauth2.googleapis.com")return Json(new{access_token="synthetic-access"});
            var body=await req.Content!.ReadAsStringAsync();Assert.DoesNotContain("PRIVATE PATIENT NAME",body);Assert.DoesNotContain(contact.Phone,body);Assert.DoesNotContain(contact.Id.ToString(),body);
            using var payload=JsonDocument.Parse(body);Assert.Equal(GoogleCalendarClient.EventId(scope.Id,appointment),payload.RootElement.GetProperty("id").GetString());
            calls.Add(req.Method.Method);return new HttpResponseMessage(req.Method==HttpMethod.Put?HttpStatusCode.NotFound:HttpStatusCode.Created);
        });
        var count=await new GoogleCalendarClient(new HttpClient(google),config,db,scope,hospital).Sync(DateOnly.FromDateTime(DateTime.UtcNow),1);
        Assert.Equal(1,count);Assert.Equal(new[]{"PUT","POST"},calls);
    }
    [Theory][InlineData("cancelled-by-patient")][InlineData("cancelled-by-clinic")][InlineData("entered-in-error")]public async Task GoogleCancellationDoesNotRecreateMissingEvent(string status){
        var tenant=await db.Tenants.SingleAsync(x=>x.Id==scope.Id);tenant.GoogleRefreshToken="synthetic-refresh";tenant.GoogleCalendarId="synthetic-calendar";await db.SaveChangesAsync();
        var config=GoogleConfig();var methods=new List<string>();
        var hospital=new HospitalClient(new HttpClient(new Fake(_=>Task.FromResult(Json(new{rows=new[]{new{appointmentId=Guid.NewGuid(),scheduledStart=DateTimeOffset.UtcNow,durationMinutes=30,status}}})))),config);
        var google=new Fake(req=>{if(req.RequestUri!.Host=="oauth2.googleapis.com")return Task.FromResult(Json(new{access_token="test"}));methods.Add(req.Method.Method);return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));});
        await new GoogleCalendarClient(new HttpClient(google),config,db,scope,hospital).Sync(DateOnly.FromDateTime(DateTime.UtcNow),1);Assert.Equal(new[]{"DELETE"},methods);
    }
    IConfiguration GoogleConfig(){
        string Encode(object value)=>Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)).TrimEnd('=').Replace('+','-').Replace('/','_');
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            ["GOOGLE_CLIENT_ID"]="synthetic",["GOOGLE_CLIENT_SECRET"]="synthetic",
            [$"Hospital:Tenants:{scope.Id}:BaseUrl"]="https://hospital.example.invalid",
            [$"Hospital:Tenants:{scope.Id}:AccessToken"]=Encode(new{alg="none"})+"."+Encode(new{tenant_id=scope.Id,exp=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()})+".synthetic"
        }).Build();
    }

    async Task<Member> TeamMember(string role="agent", bool disabled=false, Guid? tenant=null) {
        var member=new Member{TenantId=tenant??scope.Id,Subject="team-"+Guid.NewGuid(),Name="Compañero sintético",Role=role,Disabled=disabled};
        db.Add(member);await db.SaveChangesAsync();return member;
    }
    CurrentUser TeamAdmin()=>new(){Subject="test-admin",Name="Administrador sintético",Role="admin"};
    ConversationService NoSend()=>Service(new Fake(_=>throw new Exception("Assignment must never send messages")));
    static int? Code(Microsoft.AspNetCore.Http.IResult result)=>(result as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode;
    [Fact]public async Task TeamAssignsMultipleConversationsAndLogsPreviousOwner(){
        var member=await TeamMember();
        var extraChannel=new Channel{TenantId=scope.Id,Name="Another test number",PhoneNumberId="team-"+Guid.NewGuid()};db.Add(extraChannel);var second=new Conversation{TenantId=scope.Id,ContactId=contact.Id,ChannelId=extraChannel.Id,State="pending"};db.Add(second);await db.SaveChangesAsync();
        var result=await TeamEndpoints.Assign(new([new(conversation.Id,0),new(second.Id,0)],member.Subject),db,TeamAdmin(),scope,NoSend());
        Assert.Equal(200,Code(result));Assert.Equal(member.Subject,conversation.AssignedTo);Assert.Equal(member.Subject,second.AssignedTo);
        Assert.Equal("human",conversation.Status);Assert.Equal("pending",second.State);Assert.Equal(2,await db.Activities.CountAsync(x=>x.Kind=="assignment"));
        Assert.All(await db.Activities.Where(x=>x.Kind=="assignment").ToListAsync(),a=>Assert.Contains("Sin asignar → Compañero sintético",a.Body));
    }
    [Fact]public async Task StaleBatchChangesNoneOfItsConversations(){
        var member=await TeamMember();var extraChannel=new Channel{TenantId=scope.Id,Name="Another test number",PhoneNumberId="team-"+Guid.NewGuid()};db.Add(extraChannel);var second=new Conversation{TenantId=scope.Id,ContactId=contact.Id,ChannelId=extraChannel.Id,Revision=2};db.Add(second);await db.SaveChangesAsync();
        var result=await TeamEndpoints.Assign(new([new(conversation.Id,0),new(second.Id,1)],member.Subject),db,TeamAdmin(),scope,NoSend());
        Assert.Equal(409,Code(result));Assert.Null(conversation.AssignedTo);Assert.Null(second.AssignedTo);Assert.Equal(0,await db.Activities.CountAsync(x=>x.Kind=="assignment"));
    }
    [Fact]public async Task TeamRejectsDisabledAndForeignMembers(){
        var disabled=await TeamMember(disabled:true);var foreign=Guid.NewGuid();db.Tenants.Add(new Tenant{Id=foreign,Name="Other test hospital"});await db.SaveChangesAsync();await using var foreignDb=new CrmDb(options,new TenantScope{Id=foreign},protection);var member=new Member{TenantId=foreign,Subject="other-"+Guid.NewGuid(),Name="Other hospital member"};foreignDb.Add(member);await foreignDb.SaveChangesAsync();
        foreach(var subject in new[]{disabled.Subject,member.Subject})await Assert.ThrowsAsync<ArgumentException>(()=>TeamEndpoints.Assign(new([new(conversation.Id,0)],subject),db,TeamAdmin(),scope,NoSend()));
        Assert.Null(conversation.AssignedTo);
    }
    [Fact]public async Task TeamRejectsForeignConversationWithoutPartialAssignment(){
        var member=await TeamMember();var result=await TeamEndpoints.Assign(new([new(conversation.Id,0),new(Guid.NewGuid(),0)],member.Subject),db,TeamAdmin(),scope,NoSend());
        Assert.Equal(404,Code(result));Assert.Null(conversation.AssignedTo);
    }
    [Fact]public async Task AttendantCanTransferOwnConversationButCannotStealAnother(){
        var owner=await TeamMember();var next=await TeamMember();var user=new CurrentUser{Subject=owner.Subject,Name=owner.Name,Role="agent"};
        conversation.AssignedTo=owner.Subject;await db.SaveChangesAsync();
        Assert.Equal(200,Code(await TeamEndpoints.Assign(new([new(conversation.Id,0)],next.Subject),db,user,scope,NoSend())));
        await Assert.ThrowsAsync<AccessDeniedException>(()=>TeamEndpoints.Assign(new([new(conversation.Id,conversation.Revision)],owner.Subject),db,user,scope,NoSend()));
    }
    [Fact]public async Task AttendantCannotBulkAssign(){
        var member=await TeamMember();var user=new CurrentUser{Subject=member.Subject,Role="agent"};
        await Assert.ThrowsAsync<AccessDeniedException>(()=>TeamEndpoints.Assign(new([new(conversation.Id,0),new(Guid.NewGuid(),0)],member.Subject),db,user,scope,NoSend()));
    }
    [Fact]public async Task HumanSendRechecksOwnerAfterConcurrentTransfer(){
        conversation.AssignedTo="original-attendant";await db.SaveChangesAsync();
        await using(var other=new CrmDb(options,scope,protection))await other.Conversations.Where(c=>c.Id==conversation.Id).ExecuteUpdateAsync(s=>s.SetProperty(c=>c.AssignedTo,"next-attendant").SetProperty(c=>c.Revision,1));
        await Assert.ThrowsAsync<ArgumentException>(()=>NoSend().Send(conversation.Id,"Must not send","human","transfer-race",actingSubject:"original-attendant"));
        Assert.Single(await db.Messages.ToListAsync());
    }
    ConversationService Service(Fake k)=>new(db,scope,new KapsoClient(new HttpClient(k),config));
    AgentRuntime Runtime(Fake ai,Fake k)=>new(new HttpClient(ai),config,db,scope,new HospitalClient(new HttpClient(new Fake(_=>throw new Exception("Unexpected hospital request"))),config),Service(k),new KapsoClient(new HttpClient(k),config));
    static Fake Sending()=>new(_=>Task.FromResult(Json(new{messages=new[]{new{id="out-"+Guid.NewGuid()}}})));
    static HttpResponseMessage Reply(string text)=>Json(new{choices=new[]{new{message=new{role="assistant",content=text}}}});
    static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    sealed class Fake(Func<HttpRequestMessage,Task<HttpResponseMessage>> handler):HttpMessageHandler {public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct){Calls++;return handler(req);}}
}
