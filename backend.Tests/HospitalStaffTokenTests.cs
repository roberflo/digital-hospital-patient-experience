using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Recepcion;
using Recepcion.Integrations;
using Xunit;

/// <summary>MR-11 (decisión del dueño 2026-10-17): what a person does on the agenda reaches Hospital with that person's own Keycloak
/// token, so Hospital's permissions and audit name them; the WhatsApp agent keeps the service account. Calls the handlers MapHospital maps.</summary>
public sealed class HospitalStaffTokenTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    readonly Guid doctor = Guid.NewGuid();
    readonly List<string?> bearers = [];
    static string Token(Guid tenant, string? sub, int seconds = 300) => "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { tenant_id = tenant, sub, exp = DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeSeconds() })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic";
    string? service, user; // built once: exp is in seconds, so a token rebuilt later is another string
    string Service => service ??= Token(h.Scope.Id, null);
    string User => user ??= Token(h.Scope.Id, "staff-1");
    static DateTimeOffset Start => new(DateTime.UtcNow.Date.AddDays(5).AddHours(16), TimeSpan.Zero);
    object Availability() => new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = doctor, clinicianName = "Dra. Sintética Rivas", placeName = "Consultorio 1", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = Start, durationMinutes = 30, takenBy = 0, offered = true } } } } } } };

    /// <summary>The harness's Hospital, plus the day agenda, recording every Authorization it is sent.</summary>
    AgentHarness.Fake Recorded(Func<HttpRequestMessage, HttpResponseMessage?>? answer = null)
    {
        var inner = new HttpMessageInvoker(h.Hospital(availability: Availability()));
        return new(async request =>
        {
            bearers.Add(request.Headers.Authorization?.Parameter);
            if (answer?.Invoke(request) is { } reply) return reply;
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/agenda/day") return AgentHarness.Json(new { rows = Array.Empty<object>() });
            if (path == "/v1/patients/search") return AgentHarness.Json(new { results = Array.Empty<object>() });
            if (path == "/v1/commercial/customers" && request.Method == HttpMethod.Get) return AgentHarness.Json(new { items = Array.Empty<object>(), total = 0, page = 1, pageSize = 100 });
            if (path == "/v1/commercial/customers" || path == $"/v1/commercial/customers/{customer}") return AgentHarness.Json(Customer());
            if (path == "/v1/commercial/quotes") return AgentHarness.Json(quote);
            if (path == "/v1/commercial/purchases" && request.Method == HttpMethod.Post) return AgentHarness.Json(new HospitalPurchase(Guid.NewGuid(), customer, opportunity!.Id, "completed", DateTimeOffset.UtcNow, "receipt", quote));
            return await inner.SendAsync(request, CancellationToken.None);
        });
    }
    HospitalClient Client(HttpMessageHandler hospital) => new(new HttpClient(hospital), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        [$"Hospital:Tenants:{h.Scope.Id}:BaseUrl"] = "https://hospital.example.invalid/", [$"Hospital:Tenants:{h.Scope.Id}:AccessToken"] = Service
    }).Build());
    static HttpContext Request(string bearer, string? key = null) { var ctx = new DefaultHttpContext(); ctx.Request.Headers.Authorization = "Bearer " + bearer; ctx.Request.Headers["Idempotency-Key"] = key ?? Guid.NewGuid().ToString(); return ctx; }
    readonly Guid customer = Guid.NewGuid();
    Opportunity? opportunity;
    readonly HospitalQuote quote = new("v1", null, null, "USD", 0, 10, 0, 10, [new(Guid.NewGuid(), 1, "S-1", "Consulta sintética", "service", 1, 10, 10, 0, 10)]);
    HospitalCustomer Customer() => new(customer, "Paciente Sintético", h.Contact.Phone, "", h.Contact.PatientId, null, null, null, "hospital_patient", 1, DateTimeOffset.UtcNow);
    /// <summary>Everything any staff route needs: a linked, consenting contact, Google and reminders set up, and a quoted opportunity.</summary>
    async Task Prepare()
    {
        await h.Link();
        var tenant = await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id);
        tenant.GoogleRefreshToken = "synthetic-refresh"; tenant.GoogleCalendarId = "synthetic@calendar"; tenant.ReminderChannelId = h.Conversation.ChannelId;
        h.Contact.ReminderConsentAt = DateTimeOffset.UtcNow;
        opportunity = new Opportunity { TenantId = h.Scope.Id, ContactId = h.Contact.Id, Title = "Consulta sintética", HospitalQuote = JsonSerializer.Serialize(quote, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        h.Db.Add(opportunity); await h.Db.SaveChangesAsync();
    }
    readonly CurrentUser staff = new() { Subject = "staff-1", Role = "agent", Name = "Recepción Sintética" };
    AppointmentInput Input(string action, Guid? appointment = null) => new(h.Contact.Id, action, appointment, doctor, Start, 30, action == "cancel" ? "patient-requested" : null);

    [Fact]
    public async Task EveryStaffRequestCarriesTheUsersTokenNeverTheServiceAccount()
    {
        await h.Link(); var hospital = Client(Recorded()); var day = DateOnly.FromDateTime(Start.UtcDateTime);

        await HospitalEndpoints.ContactAppointments(h.Contact.Id, day, day, Request(User), h.Db, hospital, h.Scope);
        await HospitalEndpoints.Agenda(day, Request(User), hospital, h.Scope);
        await HospitalEndpoints.Availability(day, doctor, Request(User), hospital, h.Scope);
        await HospitalEndpoints.ChangeAppointment(Input("create"), Request(User), h.Db, hospital, h.Scope, staff);
        await HospitalEndpoints.ChangeAppointment(Input("reschedule", Guid.NewGuid()), Request(User), h.Db, hospital, h.Scope, staff);
        await HospitalEndpoints.ChangeAppointment(Input("cancel", Guid.NewGuid()), Request(User), h.Db, hospital, h.Scope, staff);

        Assert.Collection(h.HospitalWrites, w => Assert.Equal("/v1/agenda", w), w => Assert.EndsWith("/reschedule", w), w => Assert.EndsWith("/cancel", w));
        Assert.True(bearers.Count >= 10); // patient checks, booking options, the day agenda and the three writes
        Assert.All(bearers, bearer => Assert.Equal(User, bearer));
        // AsUser is a copy: the injected client still speaks as the service account.
        bearers.Clear(); await hospital.GetAgendaDayAsync(h.Scope.Id, day);
        Assert.Equal(Service, Assert.Single(bearers));
    }

    [Fact]
    public async Task TheAgentKeepsTheServiceAccount()
    {
        await h.Link(); await h.Say("patient", "MISCITAS");

        await h.Runtime(new AgentHarness.Fake(_ => throw new InvalidOperationException("The model must not be consulted")), h.Sender(), Recorded(),
            new() { [$"Hospital:Tenants:{h.Scope.Id}:AccessToken"] = Service }).Run(h.Job, CancellationToken.None);

        Assert.NotEmpty(bearers);
        Assert.All(bearers, bearer => Assert.Equal(Service, bearer));
    }

    [Fact]
    public async Task HospitalRefusingThePersonSaysSoInsteadOfBlamingTheConnection()
    {
        var hospital = Client(Recorded(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));

        var error = await Assert.ThrowsAsync<HospitalIntegrationException>(() => HospitalEndpoints.Agenda(DateOnly.FromDateTime(DateTime.UtcNow), Request(User), hospital, h.Scope));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal("Tu usuario no tiene permiso para esto en Hospital.", error.DisplayMessage);
    }

    [Fact]
    public async Task HospitalRejectingTheTokenIsAForbiddenNotALoginLoop()
    {
        var hospital = Client(Recorded(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var error = await Assert.ThrowsAsync<HospitalIntegrationException>(() => HospitalEndpoints.Agenda(DateOnly.FromDateTime(DateTime.UtcNow), Request(User), hospital, h.Scope));

        Assert.Equal(("hospital.user_token_rejected", HttpStatusCode.Forbidden), (error.Code, error.StatusCode));
    }

    [Fact]
    public async Task AnotherHospitalsTokenIsRejectedBeforeAnyRequest()
    {
        var hospital = Client(Recorded());

        var error = await Assert.ThrowsAsync<HospitalIntegrationException>(() => HospitalEndpoints.Agenda(DateOnly.FromDateTime(DateTime.UtcNow), Request(Token(Guid.NewGuid(), "staff-1")), hospital, h.Scope));

        Assert.Equal(("hospital.user_token_rejected", HttpStatusCode.Forbidden), (error.Code, error.StatusCode));
        Assert.Empty(bearers);
    }

    // Hospital refusing the person applied nothing, so the same request may be sent again once they can: a 403 or 401 on the write
    // itself, or their token refused before anything is sent.
    [Theory]
    [InlineData("create", "403")][InlineData("create", "401")][InlineData("create", "token")]
    [InlineData("reschedule", "403")][InlineData("reschedule", "401")][InlineData("reschedule", "token")]
    [InlineData("cancel", "403")][InlineData("cancel", "401")][InlineData("cancel", "token")]
    public async Task ARefusedAppointmentChangeDoesNotSpendItsKey(string action, string failure)
    {
        await h.Link(); var refuse = true; var key = Guid.NewGuid().ToString(); var appointment = action == "create" ? (Guid?)null : Guid.NewGuid();
        var hospital = Client(Recorded(r => refuse && r.Method == HttpMethod.Post && failure != "token" ? new HttpResponseMessage(failure == "403" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized) : null));

        var refused = await Assert.ThrowsAsync<HospitalIntegrationException>(() => HospitalEndpoints.ChangeAppointment(Input(action, appointment), Request(failure == "token" ? Token(Guid.NewGuid(), "staff-1") : User, key), h.Db, hospital, h.Scope, staff));
        Assert.Equal(failure == "403" ? "hospital.user_forbidden" : "hospital.user_token_rejected", refused.Code);
        refuse = false;
        var retried = await HospitalEndpoints.ChangeAppointment(Input(action, appointment), Request(User, key), h.Db, hospital, h.Scope, staff);

        Assert.Equal(StatusCodes.Status200OK, ((IStatusCodeHttpResult)retried).StatusCode);
        Assert.Single(h.HospitalWrites);
    }

    // N2: the routes as Hosting.MapRecepcionApi maps them, resolved from the host's own container; only Hospital, Google and the
    // database are doubles. A route that forgot AsUser sends the service account and fails here, whatever its static method does.
    [Theory]
    [InlineData("GET /api/hospital/agenda")][InlineData("GET /api/hospital/availability")][InlineData("GET /api/hospital/contacts/{id:guid}/appointments")]
    [InlineData("POST /api/hospital/appointments")][InlineData("POST /api/hospital/contacts/{id:guid}/patients/search")]
    [InlineData("GET /api/commercial/companies")][InlineData("GET /api/commercial/services")][InlineData("GET /api/companies")]
    [InlineData("GET /api/commercial/contacts/{id:guid}")][InlineData("POST /api/commercial/contacts/{id:guid}/sync")]
    [InlineData("POST /api/commercial/contacts/{id:guid}/link")][InlineData("POST /api/commercial/opportunities/{id:guid}/quote")]
    [InlineData("POST /api/commercial/opportunities/{id:guid}/purchase")][InlineData("POST /api/contacts/{id:guid}/patient")]
    [InlineData("POST /api/google/sync")][InlineData("POST /api/appointment-reminders/sync")]
    public async Task EveryMappedStaffRouteSendsTheUsersToken(string route)
    {
        await Prepare();
        var day = DateOnly.FromDateTime(Start.UtcDateTime).ToString("yyyy-MM-dd");
        var (query, body, role) = route switch
        {
            "GET /api/hospital/agenda" => ($"?date={day}", null, "agent"),
            "GET /api/hospital/availability" => ($"?date={day}&doctorId={doctor}", null, "agent"),
            "GET /api/hospital/contacts/{id:guid}/appointments" => ($"?from={day}&to={day}", null, "agent"),
            "POST /api/hospital/appointments" => ("", Input("create"), "agent"),
            "POST /api/hospital/contacts/{id:guid}/patients/search" => ("", new HospitalPatientSearchInput("name-tokens", "Sintético"), "agent"),
            "POST /api/google/sync" => ("", new CalendarSync(DateOnly.Parse(day), 1), "admin"),
            "POST /api/appointment-reminders/sync" => ("", null, "admin"),
            "POST /api/commercial/contacts/{id:guid}/link" => ("", new CustomerLink(customer), "agent"),
            "POST /api/commercial/opportunities/{id:guid}/quote" => ("", new QuoteInput([new(quote.Lines[0].ServiceId, 1)], null), "agent"),
            "POST /api/commercial/opportunities/{id:guid}/purchase" => ("", new PurchaseConfirmation("v1", true, "receipt"), "agent"),
            "POST /api/contacts/{id:guid}/patient" => ("", new PatientLinkInput(h.Contact.PatientId!.Value), "agent"),
            _ => ("", (object?)null, "agent"),
        };

        var status = await Call(App(Recorded()), route, query, body, role, id: route.Contains("/opportunities/") ? opportunity!.Id : null);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.NotEmpty(bearers);
        Assert.All(bearers, bearer => Assert.Equal(User, bearer));
    }

    // I-1: a sync a person starts tells them Hospital refused them; it does not answer «Agenda verificada».
    [Fact]
    public async Task AReminderSyncHospitalRefusesSaysSo()
    {
        await Prepare();

        var error = await Assert.ThrowsAsync<HospitalIntegrationException>(() => Call(App(Recorded(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))), "POST /api/appointment-reminders/sync", "", null, "admin"));

        Assert.Equal(("hospital.user_forbidden", HttpStatusCode.Forbidden), (error.Code, error.StatusCode));
    }

    // I-2: a person's token lasts 300 s. A reminder sync needs up to two minutes of it, so with less it is refused before Hospital is asked.
    [Fact]
    public async Task AReminderSyncWithASessionAboutToEndIsRefusedUpFront()
    {
        await Prepare();

        var error = await Assert.ThrowsAsync<HospitalIntegrationException>(() => Call(App(Recorded()), "POST /api/appointment-reminders/sync", "", null, "admin", Token(h.Scope.Id, "staff-1", 90)));

        Assert.Equal("Tu sesión está por vencer; recarga y vuelve a intentar.", error.DisplayMessage);
        Assert.Empty(bearers);
    }

    // I-2: and a manual sync is bounded, so it ends while the token still works instead of failing contact after contact.
    [Fact]
    public async Task AManualReminderSyncStopsAtItsBudget()
    {
        await Prepare();
        using var app = App(AgentHarness.Hang(r => r.RequestUri!.AbsolutePath.StartsWith("/v1/agenda/patients/") ? null : AgentHarness.Json(new { patientId = h.Contact.PatientId, givenNames = "Paciente", familyNames = "Sintético", phone = h.Contact.Phone })));
        using var scope = app.Services.CreateScope(); scope.ServiceProvider.GetRequiredService<TenantScope>().Id = h.Scope.Id;

        var error = await Assert.ThrowsAsync<HospitalIntegrationException>(() => scope.ServiceProvider.GetRequiredService<AppointmentReminderService>().AsUser(User).SyncAsPerson(DateTimeOffset.UtcNow, CancellationToken.None, TimeSpan.FromMilliseconds(300)));

        Assert.Equal(("hospital.timeout", HttpStatusCode.GatewayTimeout), (error.Code, error.StatusCode));
    }

    // I-2: Google is written only once every day has been read from Hospital, so a token that fails midway leaves Google untouched.
    [Fact]
    public async Task AGoogleSyncReadsHospitalBeforeWritingAnything()
    {
        await Prepare(); var first = DateOnly.FromDateTime(Start.UtcDateTime);
        var row = new { appointmentId = Guid.NewGuid(), status = "booked", scheduledStart = Start, durationMinutes = 30, clinicianName = "Dra. Sintética Rivas" };
        var hospital = Recorded(r => r.RequestUri!.AbsolutePath != "/v1/agenda/day" ? null
            : r.RequestUri.Query.Contains(first.ToString("yyyy-MM-dd")) ? AgentHarness.Json(new { rows = new[] { row } }) : new HttpResponseMessage(HttpStatusCode.Forbidden));

        await Assert.ThrowsAsync<HospitalIntegrationException>(() => Call(App(hospital), "POST /api/google/sync", "", new CalendarSync(first, 2), "admin"));

        Assert.Empty(google);
    }

    // N2: the local audit of a sync a person starts names them, not the worker.
    [Fact]
    public async Task AGoogleSyncAPersonStartsIsAuditedAsThem()
    {
        await Prepare();

        await Call(App(Recorded()), "POST /api/google/sync", "", new CalendarSync(DateOnly.FromDateTime(Start.UtcDateTime), 1), "admin");

        await using var db = h.NewDb(h.Scope);
        Assert.Equal(staff.Subject, (await db.Audits.SingleAsync(a => a.Action == "google.synced")).Actor);
    }

    // N3: a purchase Hospital refuses for the person was not made, so it does not stay «en comprobación» blocking the contact.
    [Theory][InlineData(HttpStatusCode.Forbidden)][InlineData(HttpStatusCode.Unauthorized)]
    public async Task ARefusedPurchaseDoesNotLeaveTheContactBlocked(HttpStatusCode refusal)
    {
        await Prepare();
        var hospital = Recorded(r => r.RequestUri!.AbsolutePath == "/v1/commercial/purchases" && r.Method == HttpMethod.Post ? new HttpResponseMessage(refusal) : null);

        await Assert.ThrowsAsync<HospitalIntegrationException>(() => Call(App(hospital), "POST /api/commercial/opportunities/{id:guid}/purchase", "", new PurchaseConfirmation("v1", true, "receipt"), "agent", id: opportunity!.Id));

        await using var db = h.NewDb(h.Scope);
        Assert.Null((await db.Opportunities.SingleAsync(o => o.Id == opportunity.Id)).HospitalPurchaseRequest);
    }

    readonly List<string> google = [];
    WebApplication App(HttpMessageHandler hospital)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = "Host=never-opened", ["Auth:Authority"] = "https://identity.example.test/realms/hospital", ["KEY_DIRECTORY"] = Path.GetTempPath(),
            ["GOOGLE_CLIENT_ID"] = "synthetic", ["GOOGLE_CLIENT_SECRET"] = "synthetic",
            [$"Hospital:Tenants:{h.Scope.Id}:BaseUrl"] = "https://hospital.example.invalid/", [$"Hospital:Tenants:{h.Scope.Id}:AccessToken"] = Service
        });
        builder.Services.AddRecepcionServices(builder.Configuration, false);
        builder.Services.AddScoped(sp => h.NewDb(sp.GetRequiredService<TenantScope>()));
        builder.Services.AddTransient(sp => new HospitalClient(new HttpClient(hospital, false), sp.GetRequiredService<IConfiguration>()));
        // Google: a refresh that always works and a calendar that accepts everything; the day agenda is empty, so nothing is written.
        var calendar = new AgentHarness.Fake(r => { google.Add(r.Method + " " + r.RequestUri!.AbsolutePath); return Task.FromResult(AgentHarness.Json(new { access_token = "synthetic-google" })); });
        builder.Services.AddTransient(sp => new GoogleCalendarClient(new HttpClient(calendar, false), sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<CrmDb>(), sp.GetRequiredService<TenantScope>(), sp.GetRequiredService<HospitalClient>()));
        var app = builder.Build(); app.MapRecepcionApi(); return app;
    }
    async Task<int> Call(WebApplication app, string route, string query, object? body, string role, string? bearer = null, Guid? id = null)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>()
            .Single(e => string.Join("|", e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["ANY"]) + " " + e.RoutePattern.RawText == route);
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Id = h.Scope.Id;
        var user = scope.ServiceProvider.GetRequiredService<CurrentUser>(); user.Subject = staff.Subject; user.Name = staff.Name; user.Role = role;
        var ctx = Request(bearer ?? User); ctx.RequestServices = scope.ServiceProvider;
        ctx.Request.Method = route.Split(' ')[0]; ctx.Request.QueryString = new(query.Length > 0 ? query : null);
        if (route.Contains("{id:guid}")) ctx.Request.RouteValues["id"] = (id ?? h.Contact.Id).ToString();
        if (body is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            // A bare DefaultHttpContext says it can carry no body, and a JSON parameter would then be a 400 before the handler runs.
            ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody());
            ctx.Request.ContentType = "application/json"; ctx.Request.ContentLength = bytes.Length; ctx.Request.Body = new MemoryStream(bytes);
        }
        ctx.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(ctx);
        return ctx.Response.StatusCode;
    }
    sealed class HasBody : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
