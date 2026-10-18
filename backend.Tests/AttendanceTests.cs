using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Recepcion;
using Recepcion.Integrations;
using Xunit;

/// <summary>MR-44: the contact's profile shows the linked patient's no-shows, total and in a row. Hospital counts them
/// (GET v1/agenda/patients/{id}/attendance → { noShows, consecutiveNoShows }); Recepción reads them as the person (MR-11).</summary>
public sealed class AttendanceTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();

    readonly List<(string Path, string? Bearer)> reads = [];
    /// <summary>Hospital: the patient (with <paramref name="phone"/>, the contact's by default) and the attendance read, recorded.</summary>
    AgentHarness.Fake Hospital(HttpStatusCode status, object? body = null, string? phone = null) => new(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.StartsWith("/v1/patients/")) return Task.FromResult(AgentHarness.Json(new { patientId = h.Contact.PatientId, givenNames = "Paciente", familyNames = "Sintético", phone = phone ?? h.Contact.Phone }));
        lock (reads) reads.Add((path, request.Headers.Authorization?.Parameter));
        return Task.FromResult(status == HttpStatusCode.OK ? AgentHarness.Json(body!) : new HttpResponseMessage(status) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") });
    });
    string token = "";
    HttpContext Staff()
    {
        token = "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { tenant_id = h.Scope.Id, sub = "staff-1", exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic";
        var ctx = new DefaultHttpContext(); ctx.Request.Headers.Authorization = "Bearer " + token; return ctx;
    }

    [Fact]
    public async Task TheLinkedPatientsNoShowsAreReadAsThePerson()
    {
        await h.Link();
        var result = await HospitalEndpoints.ContactAttendance(h.Contact.Id, Staff(), h.Db, h.Client(Hospital(HttpStatusCode.OK, new { noShows = 3, consecutiveNoShows = 2 })), h.Scope);

        Assert.Equal(new HospitalAttendance(3, 2), Assert.IsType<Ok<HospitalAttendance>>(result).Value);
        var read = Assert.Single(reads);
        Assert.Equal($"/v1/agenda/patients/{h.Contact.PatientId:D}/attendance", read.Path);
        Assert.Equal(token, read.Bearer);
    }

    [Fact]
    public async Task WithoutALinkedPatientHospitalIsNotAsked()
    {
        var result = await HospitalEndpoints.ContactAttendance(h.Contact.Id, Staff(), h.Db, h.Client(Hospital(HttpStatusCode.OK, new { noShows = 1, consecutiveNoShows = 1 })), h.Scope);

        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Empty(reads);
    }

    /// <summary>Attendance is the patient's history: a link whose phone no longer matches is refused as the contact's appointments are.</summary>
    [Fact]
    public async Task AStaleLinkIsRefusedAsTheAppointmentsAreAndAttendanceIsNotRead()
    {
        await h.Link();
        var stale = h.Client(Hospital(HttpStatusCode.OK, new { noShows = 3, consecutiveNoShows = 2 }, phone: "50379999999"));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var appointments = await Assert.ThrowsAsync<HospitalIntegrationException>(() => HospitalEndpoints.ContactAppointments(h.Contact.Id, today, today, Staff(), h.Db, stale, h.Scope));
        reads.Clear();

        var attendance = await Assert.ThrowsAsync<HospitalIntegrationException>(() => HospitalEndpoints.ContactAttendance(h.Contact.Id, Staff(), h.Db, stale, h.Scope));

        Assert.Equal("hospital.patient_phone_mismatch", appointments.Code);
        Assert.Equal((appointments.Code, appointments.StatusCode), (attendance.Code, attendance.StatusCode));
        Assert.Empty(reads);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "hospital.user_forbidden")]
    [InlineData(HttpStatusCode.Unauthorized, "hospital.user_token_rejected")]
    public async Task APersonHospitalRefusesGetsTheUserCodes(HttpStatusCode status, string code)
    {
        await h.Link();
        var e = await Assert.ThrowsAsync<HospitalIntegrationException>(() => HospitalEndpoints.ContactAttendance(h.Contact.Id, Staff(), h.Db, h.Client(Hospital(status)), h.Scope));
        Assert.Equal(code, e.Code);
    }
}
