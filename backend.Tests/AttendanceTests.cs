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
    AgentHarness.Fake Hospital(HttpStatusCode status, object? body = null) => new(request =>
    {
        lock (reads) reads.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.Parameter));
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
