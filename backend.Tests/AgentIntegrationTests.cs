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
    [Fact] public async Task HospitalConnectionIsEncryptedAndTenantScoped()
    {
        var tenant=await db.Tenants.SingleAsync(x=>x.Id==scope.Id);
        tenant.HospitalConnection=JsonSerializer.Serialize(new Dictionary<string,string?>{["BaseUrl"]="https://new.example.com",["ClientSecret"]="synthetic-connection-secret"});await db.SaveChangesAsync();
        var raw=await db.Database.SqlQueryRaw<string>("SELECT \"HospitalConnection\" AS \"Value\" FROM \"Tenants\" WHERE \"Id\" = {0}",scope.Id).SingleAsync();
        Assert.DoesNotContain("synthetic-connection-secret",raw);
        Assert.DoesNotContain("HospitalConnection",JsonSerializer.Serialize(tenant));
        var store=new HospitalConnectionStore(db,config);
        Assert.Equal("synthetic-connection-secret",store.Section(scope.Id)["ClientSecret"]);
        Assert.Null(store.Section(Guid.NewGuid())["ClientSecret"]);
    }
    [Theory][InlineData("Administrador","true",true)][InlineData("Administrador",null,true)][InlineData("Recepción",null,false)][InlineData("Médicos",null,false)][InlineData("Administrador","false",false)]
    public async Task OnboardingUsesAuthenticatedHospitalAdministratorOnly(string role,string? enabled,bool expected)
    {
        var tid=Guid.NewGuid();var subject=Guid.NewGuid().ToString();
        var cfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["HOSPITAL_SELF_ONBOARDING"]=enabled,["Auth:Authority"]="https://identity.example.com"}).Build();
        var ctx=new Microsoft.AspNetCore.Http.DefaultHttpContext{User=new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]{new System.Security.Claims.Claim("tenant_id",tid.ToString()),new("sub",subject),new("realm_access","{\"roles\":[\""+role+"\"]}"),new("iss","https://identity.example.com"),new("name","Hospital staff")},"test"))};
        var current=new CurrentUser();var identityScope=new TenantScope();await using var identityDb=new CrmDb(options,identityScope,protection);var success=await Identity.Bind(ctx,identityDb,identityScope,current,cfg);
        Assert.Equal(expected,success);Assert.Equal(expected,await db.Tenants.AnyAsync(x=>x.Id==tid));
        if(expected){Assert.Equal("admin",current.Role);Assert.False((await db.Tenants.SingleAsync(x=>x.Id==tid)).AgentEnabled);}
    }
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
        var tool=new { id="call1",type="function",function=new {name="handoff",arguments="{\"reason\":\"Recepción debe ayudar\",\"urgent\":true}"}};
        var ai=new Fake(_=>Task.FromResult(Json(new {choices=new[]{new {message=new {role="assistant",content=(string?)null,tool_calls=new[]{tool}}}}})));
        var k=Sending();await Runtime(ai,k).Run(job,CancellationToken.None);
        Assert.Equal("human",conversation.Status);Assert.Equal(1,k.Calls);Assert.Contains(await db.Activities.ToListAsync(),x=>x.Kind=="handoff");
    }
    [Fact] public async Task GoogleCalendarSelectionUsesNamesFiltersReadOnlyAndValidatesMembership()
    {
        var tenant=await db.Tenants.SingleAsync(x=>x.Id==scope.Id);tenant.GoogleRefreshToken="synthetic-refresh";tenant.GoogleCalendarId="previous";await db.SaveChangesAsync();
        var cfg=GoogleConfig();
        var transport=new Fake(req=>{
            if(req.RequestUri!.Host=="oauth2.googleapis.com") return Task.FromResult(Json(new{access_token="synthetic-access"}));
            Assert.Contains("minAccessRole=writer",req.RequestUri.Query);
            Assert.Equal("Bearer",req.Headers.Authorization!.Scheme);
            if(req.RequestUri.Query.Contains("pageToken=")) return Task.FromResult(Json(new{items=new[]{new{id="shared",summary="Agenda Hospital",accessRole="writer"}}}));
            return Task.FromResult(Json(new{items=new[]{new{id="mine",summary="Mi agenda",accessRole="owner"},new{id="read-only",summary="Solo lectura",accessRole="reader"}},nextPageToken="second page"}));
        });
        var g=new GoogleCalendarClient(new HttpClient(transport),cfg,db,scope,new HospitalClient(new HttpClient(),cfg));
        var choices=await g.Calendars(); Assert.Equal(2,choices.Count); Assert.Contains(choices,x=>x.Name=="Agenda Hospital");
        await Assert.ThrowsAsync<ArgumentException>(()=>g.SelectCalendar("foreign-calendar")); Assert.Equal("previous",tenant.GoogleCalendarId);
        await Assert.ThrowsAsync<ArgumentException>(()=>g.SelectCalendar("read-only"));
        await g.SelectCalendar("shared"); Assert.Equal("shared",tenant.GoogleCalendarId);
        // A different hospital never inherits the first hospital's Google credentials.
        var other=new TenantScope{Id=Guid.NewGuid()}; await using var otherDb=new CrmDb(options,other,protection);
        otherDb.Tenants.Add(new Tenant{Id=other.Id,Name="Other hospital"});await otherDb.SaveChangesAsync();
        var isolated=new GoogleCalendarClient(new HttpClient(new Fake(_=>throw new Exception("Must not use another tenant's credentials"))),cfg,otherDb,other,new HospitalClient(new HttpClient(),cfg));
        await Assert.ThrowsAsync<ArgumentException>(()=>isolated.Calendars());
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
    async Task<ActivityFeedPage> Feed(string? q=null,string? care=null,Guid? contactId=null,DateOnly? from=null,DateOnly? to=null,int page=1) =>
        ((Microsoft.AspNetCore.Http.HttpResults.Ok<ActivityFeedPage>)await ActivityFeed.Read(db,scope,q,care,null,contactId,null,from,to,page,CancellationToken.None)).Value!;
    [Fact] public async Task ActivitySearchIncludesOldHistoryAndKeepsTenantIsolation()
    {
        contact.Name="María Sintética";
        for(var i=0;i<165;i++)db.Activities.Add(new Activity{TenantId=scope.Id,ContactId=contact.Id,ConversationId=conversation.Id,Actor="Recepción",ActorRole="agent",Body=i==0?"Seguimiento antiguo localizable":"Nota reciente",CreatedAt=DateTimeOffset.UtcNow.AddMinutes(i-200)});
        await db.SaveChangesAsync();
        Assert.Equal(165,(await Feed(q:"maria sintetica")).Total);
        Assert.Equal(1,(await Feed(q:"antiguo localizable")).Total);
        var first=await Feed();var second=await Feed(page:2);
        Assert.Equal(30,first.Items.Count);Assert.Equal(30,second.Items.Count);Assert.Empty(first.Items.Select(x=>x.Id).Intersect(second.Items.Select(x=>x.Id)));
        var otherScope=new TenantScope{Id=Guid.NewGuid()};await using var other=new CrmDb(options,otherScope,protection);
        other.Tenants.Add(new Tenant{Id=otherScope.Id,Name="Other synthetic hospital"});await other.SaveChangesAsync();
        var foreign=new Contact{TenantId=otherScope.Id,Name="Foreign patient",Phone="50370000999",PhoneHash=Guid.NewGuid().ToString()};other.Add(foreign);
        other.Activities.Add(new Activity{TenantId=otherScope.Id,ContactId=foreign.Id,Actor="Foreign",Body="Cross tenant secret"});await other.SaveChangesAsync();
        Assert.Equal(0,(await Feed(q:"Cross tenant secret")).Total);Assert.Equal(0,(await Feed(contactId:foreign.Id)).Total);
    }
    [Fact] public async Task ActivityKeepsHistoricalRolesRedactsProposalsAndJoinsLatestDelivery()
    {
        var member=new Member{TenantId=scope.Id,Subject="snapshot-"+scope.Id,Name="Synthetic Doctor",Role="doctor"};db.Add(member);
        db.Activities.Add(new Activity{TenantId=scope.Id,ContactId=contact.Id,Actor=member.Name,ActorSubject=member.Subject,ActorRole="agent",Body="Earlier receptionist action"});
        db.Activities.Add(new Activity{TenantId=scope.Id,ContactId=contact.Id,Actor=member.Name,Body="Legacy event"});
        db.Activities.Add(new Activity{TenantId=scope.Id,ContactId=contact.Id,Kind="proposal:secret-code",Actor="Agente",ActorRole="agent_ai",Body="{\"confirmation\":\"secret-code\"}"});await db.SaveChangesAsync();
        var user=new CurrentUser{Subject=member.Subject,Name=member.Name,Role="doctor"};var sender=Sending();
        var message=await Service(sender).Send(conversation.Id,"Synthetic response","human","role-snapshot",actor:user);
        await Service(sender).Send(conversation.Id,"Synthetic response","human","role-snapshot",actor:user);
        message.Status="read";await db.SaveChangesAsync();
        var feed=await Feed();Assert.Equal(4,feed.Total);Assert.Equal(1,sender.Calls);
        Assert.Contains(feed.Items,x=>x.CareType=="reception"&&x.Body=="Earlier receptionist action");
        Assert.Contains(feed.Items,x=>x.CareType=="unknown"&&x.Body=="Legacy event");
        Assert.DoesNotContain(feed.Items,x=>x.Body.Contains("secret-code")||x.Kind.Contains("secret-code"));
        Assert.Equal(0,(await Feed(q:"secret-code")).Total);
        var doctor=Assert.Single((await Feed(care:"doctor")).Items);Assert.Equal("read",doctor.DeliveryStatus);Assert.Equal(conversation.Id,doctor.ConversationId);
    }
    [Fact] public async Task ActivityDateFiltersUseHospitalCalendarDays()
    {
        var tenant=await db.Tenants.SingleAsync(x=>x.Id==scope.Id);tenant.TimeZone="America/El_Salvador";
        foreach(var time in new[]{"2026-10-03T05:59:59Z","2026-10-03T06:00:00Z","2026-10-04T05:59:59Z","2026-10-04T06:00:00Z"})db.Activities.Add(new Activity{TenantId=scope.Id,Body="Calendar boundary",CreatedAt=DateTimeOffset.Parse(time)});
        await db.SaveChangesAsync();Assert.Equal(2,(await Feed(from:new(2026,10,3),to:new(2026,10,3))).Total);
        await Assert.ThrowsAsync<ArgumentException>(()=>Feed(from:new(2026,10,4),to:new(2026,10,3)));
    }
    [Fact] public async Task ManualFlagAllowsHumanRepliesButNeverAutomaticReplies()
    {
        config["SEND_ENABLED"]="false";config["KAPSO_MANUAL_SEND_ENABLED"]="true";var sender=Sending();var service=Service(sender);
        await Assert.ThrowsAsync<ArgumentException>(()=>service.Send(conversation.Id,"Blocked automatic","agent","auto-blocked"));
        Assert.Equal(0,sender.Calls);Assert.Single(await db.Messages.ToListAsync());
        var sent=await service.Send(conversation.Id,"Manual reply","human","manual-allowed");Assert.Equal("sent",sent.Status);Assert.Equal(1,sender.Calls);
        var channel=await db.Channels.SingleAsync();channel.Enabled=false;await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(()=>service.Send(conversation.Id,"Disabled channel","human","disabled-blocked"));Assert.Equal(1,sender.Calls);
        channel.Enabled=true;config["KAPSO_MANUAL_SEND_ENABLED"]="false";await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(()=>service.Send(conversation.Id,"Disabled manual","human","manual-blocked"));Assert.Equal(1,sender.Calls);
    }
    [Fact] public async Task ReminderSyncDetectsHospitalChangesAndNeverDuplicates()
    {
        var now=new DateTimeOffset(2026,10,3,15,0,0,TimeSpan.Zero);var start=now.AddDays(1).AddHours(1);var appointment=Guid.NewGuid();var state="booked";
        var tenant=await db.Tenants.SingleAsync(t=>t.Id==scope.Id);tenant.ReminderChannelId=conversation.ChannelId;
        contact.PatientId=Guid.NewGuid();contact.ReminderConsentAt=now;await db.SaveChangesAsync();
        var cfg=GoogleConfig();var h=new HospitalClient(new HttpClient(new Fake(req=>Task.FromResult(req.RequestUri!.AbsolutePath.StartsWith("/v1/patients/")?Json(new{patientId=contact.PatientId,phone=contact.Phone}):Json(new{rows=new[]{new{appointmentId=appointment,scheduledStart=start,status=state}}})))),cfg);
        var k=new Fake(_=>throw new Exception("Sync must not send"));var service=new AppointmentReminderService(db,scope,h,new KapsoClient(new HttpClient(k),cfg),Service(k),cfg);
        await service.Sync(now);await service.Sync(now);Assert.Equal(2,await db.AppointmentReminders.CountAsync());Assert.All(await db.AppointmentReminders.ToListAsync(),r=>Assert.Equal("pending",r.Status));
        start=start.AddDays(1);await service.Sync(now);Assert.Equal(2,await db.AppointmentReminders.CountAsync(r=>r.Status=="cancelled"));Assert.Equal(2,await db.AppointmentReminders.CountAsync(r=>r.Status=="pending"));
        state="cancelled-by-patient";await service.Sync(now);Assert.All(await db.AppointmentReminders.ToListAsync(),r=>Assert.Equal("cancelled",r.Status));Assert.Equal(0,k.Calls);
    }
    [Fact] public async Task InterruptedReminderShowsUncertainDeliveryWithoutRetry()
    {
        var now=DateTimeOffset.UtcNow;
        var tenant=await db.Tenants.SingleAsync(t=>t.Id==scope.Id);tenant.ReminderChannelId=conversation.ChannelId;
        var message=new Message{TenantId=scope.Id,ConversationId=conversation.Id,Sender="system",Type="template",Status="sending",Body="Synthetic interrupted reminder"};db.Add(message);
        var row=new AppointmentReminder{TenantId=scope.Id,ContactId=contact.Id,PatientId=Guid.NewGuid(),ChannelId=conversation.ChannelId,AppointmentId=Guid.NewGuid(),MessageId=message.Id,StartsAt=now.AddHours(1),DueAt=now.AddMinutes(-11),AttemptedAt=now.AddMinutes(-11),Status="sending"};db.Add(row);await db.SaveChangesAsync();
        var never=new Fake(_=>throw new Exception("Interrupted sends must not retry"));
        var service=new AppointmentReminderService(db,scope,new HospitalClient(new HttpClient(never),config),new KapsoClient(new HttpClient(never),config),Service(never),config);
        await service.Sync(now);await service.Dispatch(row.Id,now);
        await db.Entry(message).ReloadAsync();Assert.Equal("uncertain",row.Status);Assert.Equal("uncertain",message.Status);Assert.Equal(0,never.Calls);
    }
    [Theory][InlineData("sent")][InlineData("timeout")][InlineData("cancelled")][InlineData("consent")][InlineData("template")][InlineData("paused")][InlineData("phone")]
    public async Task ReminderDispatchVerifiesCurrentFactsAndDoesNotReplay(string scenario)
    {
        var now=DateTimeOffset.UtcNow;var start=now.AddHours(1);var appointment=Guid.NewGuid();
        var tenant=await db.Tenants.SingleAsync(t=>t.Id==scope.Id);tenant.ReminderChannelId=conversation.ChannelId;tenant.RemindersEnabled=true;contact.Name="PRIVATE PATIENT SENTINEL";
        contact.PatientId=Guid.NewGuid();contact.ReminderConsentAt=scenario=="consent"?null:now;
        var reminder=new AppointmentReminder{TenantId=scope.Id,ContactId=contact.Id,PatientId=contact.PatientId.Value,ChannelId=conversation.ChannelId,AppointmentId=appointment,StartsAt=start,DueAt=now,Window="hour_before"};db.Add(reminder);await db.SaveChangesAsync();
        var cfg=new ConfigurationBuilder().AddConfiguration(GoogleConfig()).AddConfiguration(config).AddInMemoryCollection(new Dictionary<string,string?>{{"REMINDERS_SEND_ENABLED",scenario=="paused"?"false":"true"}}).Build();
        var h=new HospitalClient(new HttpClient(new Fake(req=>Task.FromResult(req.RequestUri!.AbsolutePath.StartsWith("/v1/patients/")?Json(new{patientId=contact.PatientId,phone=scenario=="phone"?"50370000999":contact.Phone}):Json(new{rows=new[]{new{appointmentId=appointment,scheduledStart=start,status=scenario=="cancelled"?"cancelled-by-patient":"booked"}}})))),cfg);
        var sends=0;var k=new Fake(async req=>{
            if(req.Method==HttpMethod.Get)return req.RequestUri!.AbsolutePath.Contains("phone_numbers")?Json(new{data=new{business_account_id="synthetic-waba"}}):Json(new{data=new[]{new{name=tenant.ReminderHourTemplate,language="es",status=scenario=="template"?"PENDING":"APPROVED",category="UTILITY",parameter_format="NAMED",components=new[]{new{type="BODY",text="Cita en {{hospital}} el {{fecha}} a las {{hora}}. BAJA"}}}}});
            sends++;using var payload=JsonDocument.Parse(await req.Content!.ReadAsStringAsync());Assert.Equal("template",payload.RootElement.GetProperty("type").GetString());Assert.Equal(contact.Phone,payload.RootElement.GetProperty("to").GetString());Assert.DoesNotContain(contact.Name,payload.RootElement.GetRawText());
            if(scenario=="timeout")throw new HttpRequestException("Synthetic timeout");return Json(new{messages=new[]{new{id="reminder-"+Guid.NewGuid()}}});
        });
        var service=new AppointmentReminderService(db,scope,h,new KapsoClient(new HttpClient(k),cfg),Service(k),cfg);
        if(scenario=="phone") {await Assert.ThrowsAsync<HospitalIntegrationException>(()=>service.Dispatch(reminder.Id,now));Assert.Equal(0,sends);return;}
        await service.Dispatch(reminder.Id,now);await service.Dispatch(reminder.Id,now);
        Assert.Equal(scenario is "sent" or "timeout"?1:0,sends);
        Assert.Equal(scenario switch{"sent"=>"sent","timeout"=>"uncertain","cancelled" or "consent"=>"cancelled",_=>"pending"},reminder.Status);
        if(sends==1)Assert.Single(await db.Activities.Where(a=>a.Kind=="appointment_reminder").ToListAsync());
    }
    [Theory][InlineData("create")][InlineData("reschedule")][InlineData("cancel")]
    public async Task AgentAppointmentActionsWaitForConfirmationAndReachHospital(string action)
    {
        var patient=Guid.NewGuid();var doctor=Guid.NewGuid();var appointment=Guid.NewGuid();var start=DateTimeOffset.UtcNow.AddDays(3);contact.PatientId=patient;await db.SaveChangesAsync();
        var cfg=new ConfigurationBuilder().AddConfiguration(GoogleConfig()).AddConfiguration(config).Build();var writes=new List<string>();
        var hospital=new HospitalClient(new HttpClient(new Fake(req=>{
            var path=req.RequestUri!.AbsolutePath;
            if(path.StartsWith("/v1/patients/"))return Task.FromResult(Json(new{patientId=patient,phone=contact.Phone}));
            if(path=="/v1/agenda/booking-options")return Task.FromResult(Json(new{clinicalDayFrom="",clinicalDayTo="",maxDaysPerQuery=31,rollState="open",professionals=new[]{new{clinicianId=doctor,clinicianName="Synthetic Doctor",placeName="",defaultDurationMinutes=30,days=new[]{new{clinicalDay="",state="open",takenSlotCount=0,utcOffset="-06:00",slots=new[]{new{slotId="one",startsAt=start,durationMinutes=30,takenBy=0,offered=true}}}}}}}));
            if(req.Method==HttpMethod.Get)return Task.FromResult(Json(new{appointmentId=appointment,patientId=patient,visitKind="follow-up",status="booked"}));
            writes.Add(path);return Task.FromResult(Json(new{appointmentId=appointment,status="booked",overlaps=false}));
        })),cfg);
        var toolCall = new { id="proposal", type="function", function=new { name="propose_action", arguments=JsonSerializer.Serialize(new{action,appointmentId=appointment,doctorId=doctor,startsAt=start,durationMinutes=30}) } };
        var modelMessage = new { role="assistant", content=(string?)null, tool_calls=new[]{toolCall} };
        var rounds=0;var ai=new Fake(_=>Task.FromResult(++rounds==1?Json(new{choices=new[]{new{message=modelMessage}}}):Reply("Confirma la propuesta con el código indicado.")));
        var sender=Sending();var runtime=new AgentRuntime(new HttpClient(ai),cfg,db,scope,hospital,Service(sender),new KapsoClient(new HttpClient(sender),cfg));
        await runtime.Run(job,CancellationToken.None);Assert.Empty(writes);
        var proposal=await db.Activities.SingleAsync(a=>a.Kind.StartsWith("proposal:"));var code=proposal.Kind[9..];
        var incoming=new Message{TenantId=scope.Id,ConversationId=conversation.Id,Sender="patient",Body="CONFIRMAR "+code,ExternalId="confirmation-"+Guid.NewGuid()};db.Add(incoming);await db.SaveChangesAsync();
        await runtime.Run(new Job{TenantId=scope.Id,ConversationId=conversation.Id,Key="agent:"+incoming.ExternalId},CancellationToken.None);
        Assert.Single(writes);Assert.Equal(action=="create"?"/v1/agenda":$"/v1/agenda/{appointment}/{action}",writes[0]);Assert.StartsWith("proposal_used:",proposal.Kind);
    }
    [Theory][InlineData("link")][InlineData("wrong_phone")][InlineData("wrong_reference")][InlineData("wrong_patient")][InlineData("doctor")][InlineData("ambiguous")][InlineData("no_purchase")][InlineData("purchase")][InlineData("shared_phone")]
    public async Task CommercialConversionRequiresHospitalEvidence(string scenario)
    {
        var customerId=Guid.NewGuid();var companyId=Guid.NewGuid();contact.PatientId=Guid.NewGuid();await db.SaveChangesAsync();
        var customer=new HospitalCustomer(customerId,"Synthetic",scenario=="wrong_phone"?"50379999999":contact.Phone,"",scenario=="wrong_patient"?Guid.NewGuid():scenario=="shared_phone"?null:contact.PatientId,scenario=="wrong_reference"?"recepcion:"+Guid.NewGuid():scenario=="shared_phone"?null:"recepcion:"+contact.Id,companyId,"Synthetic company","hospital_patient",1,DateTimeOffset.UtcNow);
        var quote=new HospitalQuote("v1",customerId,companyId,"USD",15,100,15,85,[]);
        var handler=new Fake(req=>Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("/purchases")?Json(new HospitalCommercialPage<HospitalPurchase>(scenario is "purchase" or "shared_phone"?[new(Guid.NewGuid(),customerId,Guid.NewGuid(),"completed",DateTimeOffset.UtcNow,null,quote)]:[],scenario is "purchase" or "shared_phone"?1:0,1,25)):
            req.RequestUri.AbsolutePath.EndsWith("/customers")?Json(new HospitalCommercialPage<HospitalCustomer>([customer],scenario=="ambiguous"?2:1,1,100)):Json(customer)));
        var service=new CommercialService(db,scope,new HospitalClient(new HttpClient(handler),GoogleConfig()),Service(Sending()));
        var user=new CurrentUser{Subject="commercial-test",Name="Synthetic receptionist",Role=scenario=="doctor"?"doctor":"agent"};
        if(scenario=="doctor")await Assert.ThrowsAsync<AccessDeniedException>(()=>service.Link(contact,customerId,user));
        else if(scenario.StartsWith("wrong_"))await Assert.ThrowsAsync<HospitalIntegrationException>(()=>service.Link(contact,customerId,user));
        else if(scenario=="link"){await service.Link(contact,customerId,user);await service.Link(contact,customerId,user);}
        else await service.Sync(contact,user);
        var converted=scenario is "link" or "purchase";
        Assert.Equal(converted,contact.IsCustomer);Assert.Equal(converted?1:0,await db.Activities.CountAsync(a=>a.Kind=="customer_converted"));
        if(converted){Assert.Equal(companyId,contact.HospitalCompanyId);Assert.Equal("active",contact.LifecycleStage);}
    }
    [Fact] public async Task CommercialPurchaseRecoversLostResponseAndDoesNotDuplicateActivity()
    {
        var opportunity=new Opportunity{TenantId=scope.Id,ContactId=contact.Id,Title="Synthetic consultation",Stage="won"};db.Add(opportunity);await db.SaveChangesAsync();Assert.False(contact.IsCustomer);
        var customerId=Guid.NewGuid();var purchaseId=Guid.NewGuid();var serviceId=Guid.NewGuid();var calls=0;string? first=null;
        var quote=new HospitalQuote("hospital-v1",null,null,"USD",15,100,15,85,[new(serviceId,1,"CONS","Consultation","consultation",1,100,100,15,85)]);
        var customer=new HospitalCustomer(customerId,contact.Name,contact.Phone,"",null,"recepcion:"+contact.Id,null,null,"purchase",1,DateTimeOffset.UtcNow);
        var handler=new Fake(async req=>{
            if(req.RequestUri!.AbsolutePath.EndsWith("/quotes"))return Json(quote);
            if(req.Method==HttpMethod.Get)return Json(customer);
            var body=await req.Content!.ReadAsStringAsync();calls++;
            if(first is null){first=body;throw new HttpRequestException("Lost response after Hospital completed");}
            Assert.Equal(first,body);Assert.Equal(opportunity.Id,JsonDocument.Parse(body).RootElement.GetProperty("idempotencyKey").GetGuid());
            return Json(new HospitalPurchase(purchaseId,customerId,opportunity.Id,"completed",DateTimeOffset.UtcNow,"receipt",quote));
        });
        var commercial=new CommercialService(db,scope,new HospitalClient(new HttpClient(handler),GoogleConfig()),Service(Sending()));var user=new CurrentUser{Subject="test",Name="Synthetic",Role="agent"};
        await commercial.Quote(opportunity,contact,[new(serviceId,1)],null,user);Assert.Equal(85,opportunity.Value);Assert.False(contact.IsCustomer);
        await Assert.ThrowsAsync<ArgumentException>(()=>commercial.Purchase(opportunity,contact,new("hospital-v1",false,"receipt"),user));Assert.Equal(0,calls);
        await Assert.ThrowsAsync<HttpRequestException>(()=>commercial.Purchase(opportunity,contact,new("hospital-v1",true,"receipt"),user));Assert.False(contact.IsCustomer);Assert.NotNull(opportunity.HospitalPurchaseRequest);
        await Assert.ThrowsAsync<ArgumentException>(()=>commercial.Link(contact,Guid.NewGuid(),user));
        Assert.False(await commercial.Sync(contact,user));
        await Assert.ThrowsAsync<ArgumentException>(()=>commercial.Quote(opportunity,contact,[new(serviceId,2)],null,user));
        await commercial.Purchase(opportunity,contact,new("hospital-v1",true,"receipt"),user);await commercial.Purchase(opportunity,contact,new("hospital-v1",true,"receipt"),user);
        Assert.True(contact.IsCustomer);Assert.Equal(purchaseId,opportunity.HospitalPurchaseId);Assert.Equal(85,opportunity.Value);Assert.Equal("won",opportunity.Stage);
        Assert.Single(await db.Activities.Where(a=>a.Kind=="purchase").ToListAsync());Assert.Single(await db.Activities.Where(a=>a.Kind=="customer_converted").ToListAsync());
    }
    [Fact] public async Task LinkingCustomerInvalidatesAnUnsentLeadQuote()
    {
        var customerId=Guid.NewGuid();var serviceId=Guid.NewGuid();var opportunity=new Opportunity{TenantId=scope.Id,ContactId=contact.Id,Title="Synthetic"};db.Add(opportunity);await db.SaveChangesAsync();
        var quote=new HospitalQuote("lead-v1",null,null,"USD",0,100,0,100,[new(serviceId,1,"CONS","Synthetic","consultation",1,100,100,0,100)]);
        var customer=new HospitalCustomer(customerId,contact.Name,contact.Phone,"",null,null,null,null,"purchase",1,DateTimeOffset.UtcNow);var purchases=0;
        var handler=new Fake(req=>{if(req.RequestUri!.AbsolutePath.EndsWith("/quotes"))return Task.FromResult(Json(quote));if(req.Method==HttpMethod.Post){purchases++;throw new Exception("Must not commit under a duplicate customer");}return Task.FromResult(Json(customer));});
        var commercial=new CommercialService(db,scope,new HospitalClient(new HttpClient(handler),GoogleConfig()),Service(Sending()));var user=new CurrentUser{Subject="test",Name="Synthetic",Role="agent"};
        await commercial.Quote(opportunity,contact,[new(serviceId,1)],null,user);await commercial.Link(contact,customerId,user);
        await Assert.ThrowsAsync<ArgumentException>(()=>commercial.Purchase(opportunity,contact,new("lead-v1",true,"receipt"),user));Assert.Equal(0,purchases);Assert.Null(opportunity.HospitalPurchaseRequest);
    }
    [Theory][InlineData("commercial.quote_changed",false)][InlineData("commercial.customer_link_conflict",false)][InlineData("commercial.invalid_phone",false)][InlineData("commercial.idempotency_conflict",true)]
    public async Task DefinitivePurchaseRejectionAllowsCorrectionButUncertainIdentityKeepsAttempt(string code,bool pending)
    {
        var serviceId=Guid.NewGuid();var quote=new HospitalQuote("v1",null,null,"USD",0,100,0,100,[new(serviceId,1,"CONS","Synthetic","consultation",1,100,100,0,100)]);
        var opportunity=new Opportunity{TenantId=scope.Id,ContactId=contact.Id,Title="Synthetic",HospitalQuote=JsonSerializer.Serialize(quote,new JsonSerializerOptions(JsonSerializerDefaults.Web))};db.Add(opportunity);await db.SaveChangesAsync();
        var handler=new Fake(_=>{var response=Json(new{code});response.StatusCode=HttpStatusCode.Conflict;return Task.FromResult(response);});
        var commercial=new CommercialService(db,scope,new HospitalClient(new HttpClient(handler),GoogleConfig()),Service(Sending()));
        var failure=await Assert.ThrowsAsync<HospitalIntegrationException>(()=>commercial.Purchase(opportunity,contact,new("v1",true,"receipt"),new(){Subject="test",Name="Synthetic",Role="agent"}));
        Assert.Equal(code,failure.Code);Assert.Equal(pending,opportunity.PurchasePending);Assert.False(contact.IsCustomer);
    }
    ConversationService Service(Fake k)=>new(db,scope,new KapsoClient(new HttpClient(k),config));
    AgentRuntime Runtime(Fake ai,Fake k)=>new(new HttpClient(ai),config,db,scope,new HospitalClient(new HttpClient(new Fake(_=>throw new Exception("Unexpected hospital request"))),config),Service(k),new KapsoClient(new HttpClient(k),config));
    static Fake Sending()=>new(_=>Task.FromResult(Json(new{messages=new[]{new{id="out-"+Guid.NewGuid()}}})));
    static HttpResponseMessage Reply(string text)=>Json(new{choices=new[]{new{message=new{role="assistant",content=text}}}});
    static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    sealed class Fake(Func<HttpRequestMessage,Task<HttpResponseMessage>> handler):HttpMessageHandler {public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct){Calls++;return handler(req);}}
}
