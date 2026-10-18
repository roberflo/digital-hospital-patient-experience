using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Recepcion.Integrations;
using Xunit;

/// <summary>MR-42/MR-43 (decided by the owner 2026-10-17): an appointment is a «first-visit» when the patient has no earlier appointment in Hospital
/// they came to, of any date; else a «follow-up». Hospital answers that in one read
/// (GET v1/agenda/patients/{id}/earlier?before=, the rule's own tests live with it); when it cannot, the appointment is a follow-up, as before.
/// The agent derives it on confirming; the team's form proposes the same and may choose any kind Hospital publishes.</summary>
public sealed class VisitKindTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public async Task InitializeAsync() { await h.Start(); await h.Link(); }
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    static readonly AgentHarness.Fake NoModel = new(_ => throw new InvalidOperationException("The model must not be consulted"));
    static readonly Guid Doctor = Guid.NewGuid();
    static readonly DateTimeOffset Start = new(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero);
    static object Agenda() => new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = Doctor, clinicianName = "Dra. Sintética", placeName = "", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = Start, durationMinutes = 30, takenBy = 0, offered = true } } } } } } };

    /// <summary>Every read of the patient's agenda, as «path?query».</summary>
    readonly List<string> reads = [];
    /// <param name="earlier">What Hospital answers: «true», «false», «404», «500» or «timeout».</param>
    AgentHarness.Fake Hospital(string earlier)
    {
        var inner = new HttpMessageInvoker(h.Hospital(availability: Agenda()));
        return new(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!path.StartsWith("/v1/agenda/patients/")) return inner.SendAsync(request, CancellationToken.None);
            lock (reads) reads.Add(path + request.RequestUri.Query);
            return earlier switch
            {
                "true" or "false" => Task.FromResult(AgentHarness.Json(new { exists = earlier == "true" })),
                "timeout" => throw new TaskCanceledException("Synthetic Hospital timeout"),
                _ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)int.Parse(earlier)) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") }),
            };
        });
    }
    string Booked() { using var body = JsonDocument.Parse(Assert.Single(h.HospitalBodies)); return body.RootElement.GetProperty("visitKind").GetString()!; }
    /// <summary>One read, of this patient, for appointments before the one being booked.</summary>
    void AskedOnceBeforeTheNewAppointment()
    {
        var read = Assert.Single(reads);
        Assert.StartsWith($"/v1/agenda/patients/{h.Contact.PatientId:D}/earlier?before=", read);
        Assert.Equal(Start, DateTimeOffset.Parse(Uri.UnescapeDataString(read[(read.IndexOf("before=") + 7)..])));
    }

    [Theory]
    [InlineData("false", "first-visit")]
    [InlineData("true", "follow-up")]
    [InlineData("404", "follow-up")]     // a Hospital without the read yet
    [InlineData("500", "follow-up")]
    [InlineData("timeout", "follow-up")] // Hospital did not answer in time: still booked, not handed off
    public async Task TheAgentBooksAFirstVisitOnlyWhenHospitalSaysThereIsNoEarlierAppointment(string earlier, string expected)
    {
        var hospital = Hospital(earlier);
        await h.Runtime(h.Model(AgentHarness.ToolCall("propose_action", new { action = "create", doctorId = Doctor, startsAt = Start, durationMinutes = 30 }), AgentHarness.Reply("Listo.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        await h.Say("patient", "CONFIRMAR " + Assert.Single(await h.Pending("proposal")).Code);
        await h.Runtime(NoModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        Assert.Equal(expected, Booked());
        Assert.Equal("agent", (await h.Fresh()).Status);
        AskedOnceBeforeTheNewAppointment();
    }

    /// <summary>A request of a member of the team, with their own token (MR-11).</summary>
    HttpContext Staff()
    {
        var token = "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { tenant_id = h.Scope.Id, sub = "staff-1", exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic";
        var ctx = new DefaultHttpContext(); ctx.Request.Headers.Authorization = "Bearer " + token; ctx.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString(); return ctx;
    }
    Task<IResult> StaffBooks(AgentHarness.Fake hospital, string? visitKind)
    {
        var ctx = Staff();
        return HospitalEndpoints.ChangeAppointment(new AppointmentInput(h.Contact.Id, "create", null, Doctor, Start, 30, VisitKind: visitKind), ctx, h.Db, h.Client(hospital), h.Scope, new CurrentUser { Subject = "recepcion-" + Guid.NewGuid(), Name = "Recepción Sintética", Role = "agent" });
    }

    [Theory]
    [InlineData(null, "false", "first-visit")]
    [InlineData(null, "true", "follow-up")]
    [InlineData(null, "500", "follow-up")]     // the suggestion failed: the appointment is still booked, as the agent does
    [InlineData(null, "timeout", "follow-up")]
    [InlineData("procedure", "false", "procedure")]
    [InlineData("same-day-urgent", "true", "same-day-urgent")]
    [InlineData("first-visit", "true", "first-visit")] // the team knows better than the agenda
    public async Task TheTeamBooksTheKindItChoseOrTheSameRule(string? chosen, string earlier, string expected)
    {
        await StaffBooks(Hospital(earlier), chosen);

        Assert.Equal(expected, Booked());
        if (chosen is null) AskedOnceBeforeTheNewAppointment(); else Assert.Empty(reads);
    }

    /// <summary>Hospital refused the person: that is theirs to see (MR-11), not a «follow-up» booked behind their back (N3).</summary>
    [Theory]
    [InlineData("403", "hospital.user_forbidden")]
    [InlineData("401", "hospital.user_token_rejected")]
    public async Task AStaffMemberHospitalRefusesIsToldSoNotGivenAFollowUp(string earlier, string code)
    {
        Assert.Equal(code, (await Assert.ThrowsAsync<HospitalIntegrationException>(() => HospitalEndpoints.VisitKind(h.Contact.Id, Start, Staff(), h.Db, h.Client(Hospital(earlier)), h.Scope))).Code);
        Assert.Equal(code, (await Assert.ThrowsAsync<HospitalIntegrationException>(() => StaffBooks(Hospital(earlier), null))).Code);

        Assert.Empty(h.HospitalWrites);
        Assert.False(await h.Db.Receipts.AnyAsync());
    }

    [Theory]
    [InlineData("first-visit-vip")]
    [InlineData("consulta")]
    public async Task AKindHospitalDoesNotPublishIsRefusedBeforeTheKeyIsSpent(string chosen)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => StaffBooks(Hospital("false"), chosen));

        Assert.Empty(h.HospitalWrites);
        Assert.False(await h.Db.Receipts.AnyAsync());
    }

    [Theory]
    [InlineData("false", "first-visit")]
    [InlineData("true", "follow-up")]
    [InlineData("404", "follow-up")]
    public async Task TheFormIsOfferedTheKindTheRuleGives(string earlier, string expected)
    {
        var result = await HospitalEndpoints.VisitKind(h.Contact.Id, Start, Staff(), h.Db, h.Client(Hospital(earlier)), h.Scope);

        Assert.Equal(expected, Assert.IsType<Ok<VisitKindAnswer>>(result).Value!.VisitKind);
        AskedOnceBeforeTheNewAppointment();
    }
}
