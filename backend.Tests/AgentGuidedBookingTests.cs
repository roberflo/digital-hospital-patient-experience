using Recepcion;
using Xunit;

/// <summary>Booking is narrowed with questions before any hour is listed: the same doctor as before (someone with
/// appointments), a preferred doctor, morning or afternoon, the doctors free in that part of the day, and only then
/// that doctor's hours. A WhatsApp list holds ten rows; two doctors with a full day do not fit in it.
/// Every step is a tap and none needs the model. Synthetic people only.</summary>
public sealed class AgentGuidedBookingTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    static readonly AgentHarness.Fake NoModel = new(_ => throw new InvalidOperationException("The model must not be consulted"));
    readonly Guid rivas = Guid.NewGuid(), mejia = Guid.NewGuid();
    // The harness clinic is at UTC-6: 14–17 UTC is the morning there, 20–22 UTC the afternoon.
    static readonly int[] Morning = [14, 15, 16, 17], Afternoon = [20, 21, 22];
    static DateTimeOffset In(int days, int hourUtc) => new(DateTime.UtcNow.Date.AddDays(days).AddHours(hourUtc), TimeSpan.Zero);
    static string Day => In(5, 14).ToOffset(TimeSpan.FromHours(-6)).ToString("yyyy-MM-dd");
    static object Doctor(Guid id, string name, params int[] hoursUtc) => new { clinicianId = id, clinicianName = name, placeName = "Consultorio 1", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = hoursUtc.Select(hour => new { slotId = "s" + hour, startsAt = In(5, hour), durationMinutes = 30, takenBy = 0, offered = true }).ToArray() } } };
    static object Agenda(params object[] professionals) => new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals };
    object Both() => Agenda(Doctor(rivas, "Dra. Sintética Rivas", [.. Morning, .. Afternoon]), Doctor(mejia, "Dr. Sintético Mejía", [.. Morning, .. Afternoon]));
    async Task<string> Say(string text, HttpMessageHandler hospital) { await h.Say("patient", text); await h.Runtime(NoModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None); return h.Sent[^1]; }
    List<string> Ids() => AgentHarness.Options(h.Interactive[^1]).Select(option => option.Id).ToList();

    [Fact]
    public async Task SomeoneNewIsAskedForAPreferredDoctorFirst()
    {
        var reply = await Say("AGENDAR", h.Hospital(availability: Both()));

        Assert.Contains("¿Tienes un doctor de preferencia?", reply);
        Assert.Equal([$"VER {Day} {rivas} ?", $"VER {Day} {mejia} ?", $"VER {Day} * ?"], Ids());
    }

    [Fact]
    public async Task SomeoneWithAppointmentsIsAskedWhetherTheyWantTheSameDoctor()
    {
        await h.Link();
        var before = new { appointmentId = Guid.NewGuid(), clinicianId = mejia, clinicianName = "Dr. Sintético Mejía", scheduledStart = In(-3, 16), durationMinutes = 30, status = "attended" };

        var reply = await Say("AGENDAR", h.Hospital(appointments: [before], availability: Both()));

        Assert.Contains("¿La quieres con el mismo doctor, *Dr. Sintético Mejía*?", reply);
        Assert.Equal([$"VER {Day} {mejia} ?", $"VER {Day} + ?", $"VER {Day} * ?"], Ids());

        // «Otro doctor» shows who there is.
        Assert.Contains("¿Tienes un doctor de preferencia?", await Say($"VER {Day} + ?", h.Hospital(appointments: [before], availability: Both())));
    }

    [Fact]
    public async Task AChosenDoctorIsFollowedByMorningOrAfternoonAndThenOnlyThoseHours()
    {
        var hospital = h.Hospital(availability: Both());

        Assert.Contains("¿Te queda mejor en la mañana o en la tarde?", await Say($"VER {Day} {rivas} ?", hospital));
        Assert.Equal([$"VER {Day} {rivas} M", $"VER {Day} {rivas} T"], Ids());

        var hours = await Say($"VER {Day} {rivas} T", hospital);
        Assert.Contains("Dra. Sintética Rivas", hours); Assert.Contains("en la *tarde*", hours);
        Assert.Equal(Afternoon.Length, Ids().Count);
        Assert.All(Ids(), id => { Assert.StartsWith("CITA ", id); Assert.Contains(rivas.ToString(), id); });
    }

    [Fact]
    public async Task WithoutAPreferenceTheDoctorsFreeInThatPartOfTheDayAreNamed()
    {
        // Mejía only attends in the afternoon: he is not offered for the morning.
        var hospital = h.Hospital(availability: Agenda(Doctor(rivas, "Dra. Sintética Rivas", [.. Morning, .. Afternoon]), Doctor(mejia, "Dr. Sintético Mejía", Afternoon)));

        Assert.Contains("¿Te queda mejor en la mañana o en la tarde?", await Say($"VER {Day} * ?", hospital));
        Assert.Equal([$"VER {Day} * M", $"VER {Day} * T"], Ids());

        var afternoon = await Say($"VER {Day} * T", hospital);
        Assert.Contains("Los doctores disponibles", afternoon); Assert.Contains("en la *tarde*", afternoon);
        Assert.Equal([$"VER {Day} {rivas} T", $"VER {Day} {mejia} T"], Ids());

        // One doctor in the morning: nothing to choose, her hours come straight away.
        var morning = await Say($"VER {Day} * M", hospital);
        Assert.Contains("Dra. Sintética Rivas", morning);
        Assert.All(Ids(), id => Assert.Contains(rivas.ToString(), id));
    }

    [Fact]
    public async Task AQuestionThatWouldChooseNothingIsNotAsked()
    {
        // One doctor, one part of the day: the hours are the answer.
        await Say("AGENDAR", h.Hospital(availability: Agenda(Doctor(rivas, "Dra. Sintética Rivas", Morning))));

        Assert.Equal(Morning.Length, Ids().Count);
        Assert.All(Ids(), id => Assert.StartsWith("CITA ", id));
    }

    [Theory]
    [InlineData("VER 2026-10-07 d2c5079c-e5da-4bb5-9029-b1e547ad8683 M")]
    [InlineData("VER 2026-10-07 * ?")]
    [InlineData("VER 2026-10-07 + ?")]
    public void ATappedStepReadsAsItsCommand(string id) =>
        Assert.Equal(id, WhatsAppContent.Inbound(System.Text.Json.JsonSerializer.SerializeToElement(new { type = "interactive", interactive = new { type = "list_reply", list_reply = new { id, title = "Dra. Sintética Rivas" } } }), default));
}
