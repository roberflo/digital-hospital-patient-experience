using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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
            if (request.RequestUri!.AbsolutePath == "/v1/agenda/day") return AgentHarness.Json(new { rows = Array.Empty<object>() });
            return await inner.SendAsync(request, CancellationToken.None);
        });
    }
    HospitalClient Client(HttpMessageHandler hospital) => new(new HttpClient(hospital), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        [$"Hospital:Tenants:{h.Scope.Id}:BaseUrl"] = "https://hospital.example.invalid/", [$"Hospital:Tenants:{h.Scope.Id}:AccessToken"] = Service
    }).Build());
    static HttpContext Request(string bearer) { var ctx = new DefaultHttpContext(); ctx.Request.Headers.Authorization = "Bearer " + bearer; ctx.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString(); return ctx; }
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
        Assert.Equal("Tu usuario no tiene permiso para esto en Hospital", error.DisplayMessage);
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
}
