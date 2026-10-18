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
    string Token(Guid tenant, string? sub) => "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { tenant_id = tenant, sub, exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic";
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
            if (path == "/v1/commercial/customers") return AgentHarness.Json(new { items = Array.Empty<object>(), total = 0, page = 1, pageSize = 100 });
            return await inner.SendAsync(request, CancellationToken.None);
        });
    }
    HospitalClient Client(HttpMessageHandler hospital) => new(new HttpClient(hospital), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        [$"Hospital:Tenants:{h.Scope.Id}:BaseUrl"] = "https://hospital.example.invalid/", [$"Hospital:Tenants:{h.Scope.Id}:AccessToken"] = Service
    }).Build());
    static HttpContext Request(string bearer, string? key = null) { var ctx = new DefaultHttpContext(); ctx.Request.Headers.Authorization = "Bearer " + bearer; ctx.Request.Headers["Idempotency-Key"] = key ?? Guid.NewGuid().ToString(); return ctx; }
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

    // N1: Hospital refusing the person applied nothing, so the same request may be sent again once they can.
    [Fact]
    public async Task ARefusedCancellationDoesNotSpendItsKey()
    {
        await h.Link(); var refuse = true; var key = Guid.NewGuid().ToString(); var appointment = Guid.NewGuid();
        var hospital = Client(Recorded(_ => refuse ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null));

        await Assert.ThrowsAsync<HospitalIntegrationException>(() => HospitalEndpoints.ChangeAppointment(Input("cancel", appointment), Request(User, key), h.Db, hospital, h.Scope, staff));
        refuse = false;
        var retried = await HospitalEndpoints.ChangeAppointment(Input("cancel", appointment), Request(User, key), h.Db, hospital, h.Scope, staff);

        Assert.Equal(StatusCodes.Status200OK, ((IStatusCodeHttpResult)retried).StatusCode);
        Assert.EndsWith("/cancel", Assert.Single(h.HospitalWrites));
    }

    // N2: the routes as Hosting.MapRecepcionApi maps them, resolved from the host's own container; only Hospital, Google and the
    // database are doubles. A route that forgot AsUser sends the service account and fails here, whatever its static method does.
    [Theory]
    [InlineData("GET /api/hospital/agenda")][InlineData("GET /api/hospital/availability")][InlineData("GET /api/hospital/contacts/{id:guid}/appointments")]
    [InlineData("POST /api/hospital/appointments")][InlineData("POST /api/hospital/contacts/{id:guid}/patients/search")]
    [InlineData("GET /api/commercial/companies")][InlineData("GET /api/commercial/services")][InlineData("GET /api/companies")]
    [InlineData("GET /api/commercial/contacts/{id:guid}")][InlineData("POST /api/commercial/contacts/{id:guid}/sync")]
    [InlineData("POST /api/google/sync")][InlineData("POST /api/appointment-reminders/sync")]
    public async Task EveryMappedStaffRouteSendsTheUsersToken(string route)
    {
        await h.Link();
        var tenant = await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id);
        tenant.GoogleRefreshToken = "synthetic-refresh"; tenant.GoogleCalendarId = "synthetic@calendar"; tenant.ReminderChannelId = h.Conversation.ChannelId;
        h.Contact.ReminderConsentAt = DateTimeOffset.UtcNow; await h.Db.SaveChangesAsync();
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
            _ => ("", (object?)null, "agent"),
        };

        var status = await Call(App(Recorded()), route, query, body, role);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.NotEmpty(bearers);
        Assert.All(bearers, bearer => Assert.Equal(User, bearer));
    }

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
        var google = new AgentHarness.Fake(_ => Task.FromResult(AgentHarness.Json(new { access_token = "synthetic-google" })));
        builder.Services.AddTransient(sp => new GoogleCalendarClient(new HttpClient(google, false), sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<CrmDb>(), sp.GetRequiredService<TenantScope>(), sp.GetRequiredService<HospitalClient>()));
        var app = builder.Build(); app.MapRecepcionApi(); return app;
    }
    async Task<int> Call(WebApplication app, string route, string query, object? body, string role)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>()
            .Single(e => string.Join("|", e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["ANY"]) + " " + e.RoutePattern.RawText == route);
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Id = h.Scope.Id;
        var user = scope.ServiceProvider.GetRequiredService<CurrentUser>(); user.Subject = staff.Subject; user.Name = staff.Name; user.Role = role;
        var ctx = Request(User); ctx.RequestServices = scope.ServiceProvider;
        ctx.Request.Method = route.Split(' ')[0]; ctx.Request.QueryString = new(query.Length > 0 ? query : null);
        if (route.Contains("{id:guid}")) ctx.Request.RouteValues["id"] = h.Contact.Id.ToString();
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
