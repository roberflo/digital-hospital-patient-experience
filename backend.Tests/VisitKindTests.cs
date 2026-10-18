using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>MR-42 (decided by the owner 2026-10-17): an appointment is a «first-visit» when the patient has no earlier appointment in Hospital,
/// else a «follow-up». The agent derives it on confirming; the team's form proposes the same and may choose any kind Hospital publishes.</summary>
public sealed class VisitKindTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public async Task InitializeAsync() { await h.Start(); await h.Link(); }
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    static readonly AgentHarness.Fake NoModel = new(_ => throw new InvalidOperationException("The model must not be consulted"));
    static readonly Guid Doctor = Guid.NewGuid();
    static readonly DateTimeOffset Start = new(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero);
    static object Agenda() => new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = Doctor, clinicianName = "Dra. Sintética", placeName = "", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = Start, durationMinutes = 30, takenBy = 0, offered = true } } } } } } };

    /// <summary>The harness's Hospital, with a patient agenda that answers only the days asked, as Hospital does (31 at most).</summary>
    /// <summary>The earliest day any patient-agenda query asked for.</summary>
    DateOnly? earliest;
    AgentHarness.Fake Hospital(int[] days, string status) => Hospital(days.Select(d => $"{d} {status}").ToArray());
    /// <param name="appointments">«days status», days from now: «-1095 booked» is an appointment three years ago.</param>
    AgentHarness.Fake Hospital(string[] appointments)
    {
        var rows = appointments.Select(a => a.Split(' ')).Select(a => (At: DateTimeOffset.UtcNow.AddDays(int.Parse(a[0])), Status: a[1])).ToList();
        var inner = new HttpMessageInvoker(h.Hospital(availability: Agenda()));
        return new(async request =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/v1/agenda/patients/"))
            {
                var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(x => x.Split('=')).ToDictionary(x => x[0], x => DateOnly.Parse(x[1]));
                Assert.True(query["to"].DayNumber - query["from"].DayNumber <= 30); // Hospital refuses more (GetRangeViewQuery.MaxDaysPerQuery)
                if (earliest is null || query["from"] < earliest) earliest = query["from"];
                return AgentHarness.Json(new { rows = rows.Where(r => DateOnly.FromDateTime(r.At.UtcDateTime) is var day && day >= query["from"] && day <= query["to"])
                    .Select(r => new { appointmentId = Guid.NewGuid(), clinicianId = Doctor, clinicianName = "Dra. Sintética", scheduledStart = r.At, durationMinutes = 30, status = r.Status }).ToArray() });
            }
            return await inner.SendAsync(request, CancellationToken.None);
        });
    }
    string Booked() { using var body = JsonDocument.Parse(Assert.Single(h.HospitalBodies)); return body.RootElement.GetProperty("visitKind").GetString()!; }

    // Owner, 2026-10-17: any earlier appointment of any date counts, except a cancelled one. Statuses as Hospital publishes them (AgendaContracts).
    public static TheoryData<string[], string> History => new()
    {
        { [], "first-visit" },
        { ["-10 booked"], "follow-up" },
        { ["-10 fulfilled"], "follow-up" },
        { ["-10 no-show"], "follow-up" },                                  // an appointment that existed, whatever came of it
        { ["-10 cancelled-by-patient", "-400 cancelled-by-clinic"], "first-visit" }, // only cancelled ones: none took place
        { ["-60 booked"], "follow-up" },                                   // further back than one query of Hospital's
        { ["-1095 fulfilled"], "follow-up" },                              // three years ago, not cancelled
        { ["-1095 fulfilled", "-5 cancelled-by-patient"], "follow-up" },    // the cancelled one is not the only one
        { ["1 booked"], "follow-up" },                                     // booked earlier than this one, still ahead
        { ["10 booked"], "first-visit" },                                  // later than this one: not an earlier appointment
    };

    [Theory, MemberData(nameof(History))]
    public async Task TheAgentBooksAFirstVisitOnlyForAPatientWithNoEarlierAppointment(string[] appointments, string expected)
    {
        var hospital = Hospital(appointments);
        await h.Runtime(h.Model(AgentHarness.ToolCall("propose_action", new { action = "create", doctorId = Doctor, startsAt = Start, durationMinutes = 30 }), AgentHarness.Reply("Listo.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        await h.Say("patient", "CONFIRMAR " + Assert.Single(await h.Pending("proposal")).Code);
        await h.Runtime(NoModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        Assert.Equal(expected, Booked());
    }

    /// <summary>The walk back stops at the first day this hospital can hold an appointment (Hospital:Tenants:{id}:AgendaSince); 2020-01-01 if unset.</summary>
    [Theory]
    [InlineData(null, "2020-01-01")]
    [InlineData(-200, null)]
    public async Task TheAgendaIsReadBackToTheHospitalsFirstDay(int? sinceDays, string? floor)
    {
        var since = floor is not null ? DateOnly.Parse(floor) : DateOnly.FromDateTime(DateTime.UtcNow.AddDays(sinceDays!.Value));
        var extra = sinceDays is null ? null : new Dictionary<string, string?> { [$"Hospital:Tenants:{h.Scope.Id}:AgendaSince"] = since.ToString("yyyy-MM-dd") };
        var hospital = Hospital([$"{since.AddDays(-100).DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber} fulfilled"]); // before the floor: not read
        await h.Runtime(h.Model(AgentHarness.ToolCall("propose_action", new { action = "create", doctorId = Doctor, startsAt = Start, durationMinutes = 30 }), AgentHarness.Reply("Listo.")), h.Sender(), hospital, extra).Run(h.Job, CancellationToken.None);
        await h.Say("patient", "CONFIRMAR " + Assert.Single(await h.Pending("proposal")).Code);
        earliest = null;
        await h.Runtime(NoModel, h.Sender(), hospital, extra).Run(h.Job, CancellationToken.None);

        Assert.Equal("first-visit", Booked());
        Assert.Equal(since, earliest); // read down to that day, and not before
    }

    [Fact]
    public async Task AHistoryHospitalCannotGiveStillBooksTheAppointmentAsBefore()
    {
        var inner = new HttpMessageInvoker(h.Hospital(availability: Agenda()));
        var hospital = new AgentHarness.Fake(request => request.RequestUri!.AbsolutePath.StartsWith("/v1/agenda/patients/") ? Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)) : inner.SendAsync(request, CancellationToken.None));
        await h.Runtime(h.Model(AgentHarness.ToolCall("propose_action", new { action = "create", doctorId = Doctor, startsAt = Start, durationMinutes = 30 }), AgentHarness.Reply("Listo.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        await h.Say("patient", "CONFIRMAR " + Assert.Single(await h.Pending("proposal")).Code);
        await h.Runtime(NoModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        Assert.Equal("follow-up", Booked());
        Assert.Equal("agent", (await h.Fresh()).Status);
    }

    Task<IResult> StaffBooks(AgentHarness.Fake hospital, string? visitKind)
    {
        var ctx = new DefaultHttpContext(); ctx.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString();
        return HospitalEndpoints.Appointment(new AppointmentInput(h.Contact.Id, "create", null, Doctor, Start, 30, VisitKind: visitKind), ctx, h.Db, h.Client(hospital), h.Scope, new CurrentUser { Subject = "recepcion-" + Guid.NewGuid(), Name = "Recepción Sintética", Role = "agent" });
    }

    [Theory]
    [InlineData(null, new int[0], "first-visit")]
    [InlineData(null, new[] { -10 }, "follow-up")]
    [InlineData("procedure", new int[0], "procedure")]
    [InlineData("same-day-urgent", new[] { -10 }, "same-day-urgent")]
    [InlineData("first-visit", new[] { -10 }, "first-visit")] // the team knows better than the agenda
    public async Task TheTeamBooksTheKindItChoseOrTheSameRule(string? chosen, int[] days, string expected)
    {
        await StaffBooks(Hospital(days, "booked"), chosen);

        Assert.Equal(expected, Booked());
    }

    [Theory]
    [InlineData("first-visit-vip")]
    [InlineData("consulta")]
    public async Task AKindHospitalDoesNotPublishIsRefusedBeforeTheKeyIsSpent(string chosen)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => StaffBooks(Hospital([], "booked"), chosen));

        Assert.Empty(h.HospitalWrites);
        Assert.False(await h.Db.Receipts.AnyAsync());
    }

    [Theory]
    [InlineData(new int[0], "first-visit")]
    [InlineData(new[] { -10 }, "follow-up")]
    public async Task TheFormIsOfferedTheKindTheRuleGives(int[] days, string expected)
    {
        var result = await HospitalEndpoints.VisitKind(h.Contact.Id, Start, h.Db, h.Client(Hospital(days, "booked")), h.Scope);

        Assert.Equal(expected, Assert.IsType<Ok<VisitKindAnswer>>(result).Value!.VisitKind);
    }
}
