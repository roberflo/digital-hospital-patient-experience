using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>docs/reception-agent.md, criterios 52–54: a registered patient sees, changes and cancels their own
/// appointments by tapping, with no model. Nothing changes in Hospital before the patient confirms.</summary>
public sealed class AgentAppointmentsTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    static readonly AgentHarness.Fake NoModel = new(_ => throw new InvalidOperationException("The model must not be consulted"));
    readonly Guid doctor = Guid.NewGuid();
    static DateTimeOffset In(int days, int hourUtc) => new(DateTime.UtcNow.Date.AddDays(days).AddHours(hourUtc), TimeSpan.Zero);
    object Booked(Guid id, int days) => new { appointmentId = id, clinicianId = doctor, clinicianName = "Dra. Sintética Rivas", scheduledStart = In(days, 16), durationMinutes = 30, status = "booked" };
    object Agenda() => new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = doctor, clinicianName = "Dra. Sintética Rivas", placeName = "Consultorio 1", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = In(5, 15), durationMinutes = 30, takenBy = 0, offered = true } } } } } } };
    async Task<string> Say(string text, HttpMessageHandler? hospital = null) { await h.Say("patient", text); await h.Runtime(NoModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None); return h.Sent[^1]; }
    List<(string Id, string Title)> Options() => AgentHarness.Options(h.Interactive[^1]);

    [Fact]
    public async Task MenuOffersMyAppointments()
    {
        await Say("Hola");

        Assert.Equal(["AGENDAR", "MISCITAS", "RECETA", "PERSONA"], Options().Select(o => o.Id));
    }

    [Fact]
    public async Task OneAppointmentIsShownWithWhatCanBeDoneToIt()
    {
        await h.Link(); var appointment = Guid.NewGuid();

        var card = await Say("MISCITAS", h.Hospital(appointments: [Booked(appointment, 3)]));

        Assert.Contains("*Cita:*", card); Assert.Contains("a las 10:00", card); Assert.Contains("Dra. Sintética Rivas", card);
        Assert.Equal([$"MOVER {appointment}", $"CANCELAR {appointment}", "MENU"], Options().Select(o => o.Id));
        Assert.Empty(h.HospitalWrites);
    }

    [Fact]
    public async Task SeveralAppointmentsComeAsAListAndCancelledOnesAreLeftOut()
    {
        await h.Link(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var cancelled = new { appointmentId = Guid.NewGuid(), clinicianId = doctor, clinicianName = "Dra. Sintética Rivas", scheduledStart = In(2, 16), durationMinutes = 30, status = "cancelled-by-patient" };

        await Say("MISCITAS", h.Hospital(appointments: [Booked(first, 3), cancelled, Booked(second, 6)]));

        Assert.Equal([$"VERCITA {first}", $"VERCITA {second}"], Options().Select(o => o.Id));
    }

    [Fact]
    public async Task NoAppointmentsOffersToBookOne()
    {
        await h.Link();

        var reply = await Say("MISCITAS", h.Hospital());

        Assert.Contains("No tienes citas próximas", reply);
        Assert.Contains("AGENDAR", Options().Select(o => o.Id));
    }

    [Fact]
    public async Task CancellingIsTwoTapsAndOnlyHappensOnTheSecond()
    {
        await h.Link(); var appointment = Guid.NewGuid(); var hospital = h.Hospital(appointments: [Booked(appointment, 3)]);

        var card = await Say($"CANCELAR {appointment}", hospital);
        Assert.StartsWith("*Cancelar cita:*", card); Assert.EndsWith("¿La cancelo?", card);
        Assert.Empty(h.HospitalWrites);

        var done = await Say(Options()[0].Id, hospital);

        Assert.Equal($"/v1/agenda/{appointment}/cancel", Assert.Single(h.HospitalWrites));
        Assert.Contains("quedó cancelada", done);
        Assert.Contains(appointment.ToString(), (await h.Db.Activities.SingleAsync(a => a.ConversationId == h.Conversation.Id && a.Kind == "appointment")).Body);
    }

    [Fact]
    public async Task ChangingTheDateListsFreeHoursAndMovesThatAppointment()
    {
        await h.Link(); var appointment = Guid.NewGuid(); var hospital = h.Hospital(appointments: [Booked(appointment, 3)], availability: Agenda());

        await Say($"MOVER {appointment}", hospital);
        var slot = Assert.Single(Options());
        Assert.StartsWith($"MOVER {appointment} ", slot.Id); Assert.Contains("09:00", slot.Title);

        var card = await Say(WhatsAppContent.Inbound(JsonSerializer.SerializeToElement(new { type = "interactive", interactive = new { type = "list_reply", list_reply = new { id = slot.Id, title = slot.Title } } }), default), hospital);
        Assert.StartsWith("*Nueva fecha:*", card); Assert.EndsWith("¿La cambio?", card);
        Assert.Empty(h.HospitalWrites);

        var done = await Say("Sí", hospital);

        Assert.Equal($"/v1/agenda/{appointment}/reschedule", Assert.Single(h.HospitalWrites));
        Assert.Contains("quedó reprogramada", done); Assert.Contains("a las 09:00", done);
    }

    [Fact]
    public async Task WithoutARecordThereIsNothingToLookUp()
    {
        var reply = await Say("MISCITAS", new AgentHarness.Fake(_ => throw new InvalidOperationException("Hospital must not be consulted")));

        Assert.Contains("Aún no tienes expediente", reply);
        Assert.Contains("AGENDAR", Options().Select(o => o.Id));
    }

    [Fact]
    public async Task AnAppointmentThatIsNotThePatientsIsNeverActedOn()
    {
        await h.Link(); var foreign = Guid.NewGuid();

        var reply = await Say($"VERCITA {foreign}", h.Hospital(appointments: [Booked(Guid.NewGuid(), 3)]));

        Assert.Contains("ya no aparece", reply);
        Assert.DoesNotContain(Options(), o => o.Id.Contains(foreign.ToString()));
    }
}
