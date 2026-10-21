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
    public async Task ANewCardRetiresTheOneBeforeIt()
    {
        // Dos tarjetas abiertas a la vez se podían confirmar las dos: dos operaciones en Hospital por una sola decisión.
        await h.Link(); var first = Guid.NewGuid(); var second = Guid.NewGuid(); var hospital = h.Hospital(appointments: [Booked(first, 3), Booked(second, 6)]);
        await Say($"CANCELAR {first}", hospital); var old = Options()[0].Id;
        await Say($"CANCELAR {second}", hospital);

        Assert.Contains("ya venció", await Say(old, hospital));
        Assert.Empty(h.HospitalWrites);
        Assert.Single(await h.Pending("proposal"));
    }

    [Fact]
    public async Task AnOldReminderDoesNotActOnAnAppointmentThatMoved()
    {
        // El recordatorio era de la cita a las 10:00 del día 3; desde entonces se movió al día 4. Su «Cancelar» no la cancela: se le muestra como está ahora.
        await h.Link(); var appointment = Guid.NewGuid();
        var reminder = new AppointmentReminder { TenantId = h.Scope.Id, ContactId = h.Contact.Id, PatientId = h.Contact.PatientId!.Value, ChannelId = h.Conversation.ChannelId, AppointmentId = appointment, StartsAt = In(3, 16), DueAt = In(2, 16), Window = "day_before", Status = "sent", AttemptedAt = DateTimeOffset.UtcNow };
        h.Db.Add(reminder); await h.Db.SaveChangesAsync();

        var reply = await Say($"CANCELAR {reminder.Id}", h.Hospital(appointments: [Booked(appointment, 4)]));

        Assert.StartsWith("Tu cita cambió desde ese recordatorio.", reply);
        Assert.Equal([$"MOVER {appointment}", $"CANCELAR {appointment}", "MENU"], Options().Select(o => o.Id));
        Assert.Empty(await h.Pending("proposal"));
    }

    [Fact]
    public async Task TheDoctorIsNamedAsHospitalNamesThemNotAsTheTapSays()
    {
        // El texto de un toque lo puede escribir el paciente: el nombre del doctor de la tarjeta y del historial sale de Hospital.
        await h.Link(); var hospital = h.Hospital(availability: Agenda());

        var card = await Say($"CITA {In(5, 15).ToOffset(TimeSpan.FromHours(-6)):yyyy-MM-ddTHH:mm:sszzz} {doctor} 30 Dr. Inventado", hospital);
        Assert.Contains("*Con:* Dra. Sintética Rivas", card); Assert.DoesNotContain("Inventado", card);

        await Say(Options()[0].Id, hospital);

        var booked = (await h.Db.Activities.SingleAsync(a => a.ConversationId == h.Conversation.Id && a.Kind == "appointment")).Body;
        Assert.Contains("con Dra. Sintética Rivas", booked); Assert.DoesNotContain("Inventado", booked);
    }

    object Past(Guid id) => new { appointmentId = id, clinicianId = doctor, clinicianName = "Dra. Sintética Rivas", scheduledStart = DateTimeOffset.UtcNow.AddHours(-2), durationMinutes = 30, status = "booked" };

    [Theory]
    [InlineData("cancel")]
    [InlineData("reschedule")]
    public async Task AnAppointmentWhoseHourHasPassedIsNeitherCancelledNorMoved(string action)
    {
        // Found in production: «cancela todas menos la del jueves» cancelled, at 14:58, the appointment of 08:00 that same day.
        // What happened to an hour that has passed is whether the patient came, and that is not the patient's to rewrite.
        await h.Link(); var appointment = Guid.NewGuid(); var hospital = h.Hospital(appointments: [Past(appointment)]);
        await h.Say("patient", "Cancela mi cita de la mañana");

        await h.Runtime(h.Model(AgentHarness.ToolCall("propose_action", new { action, appointmentId = appointment, doctorId = doctor, startsAt = In(4, 16), durationMinutes = 30 }), AgentHarness.Reply("Esa cita ya pasó, así que no se puede cambiar ni cancelar.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        Assert.Empty(h.HospitalWrites);
        Assert.Empty(await h.Pending("proposal"));
        Assert.DoesNotContain(h.Sent, text => text.Contains("¿La cancelo?") || text.Contains("¿La cambio?"));
    }

    [Fact]
    public async Task ACancellationConfirmedAfterTheHourHasPassedDoesNotHappen()
    {
        // The card was sent while the appointment was still ahead; the patient taps «Confirmar» once it has started.
        await h.Link(); var appointment = Guid.NewGuid();
        await Say($"CANCELAR {appointment}", h.Hospital(appointments: [Booked(appointment, 3)]));

        var late = await Say(Options()[0].Id, h.Hospital(appointments: [Past(appointment)]));

        Assert.Empty(h.HospitalWrites);
        Assert.Contains("ya pasó", late);
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

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"appointmentId\":")]
    public async Task AnUnreadableBookingAnswerGoesToAPersonAndIsNotRepeated(string answer)
    {
        // MR-5: the proposal is consumed and Hospital may have booked: the team reconciles, the turn does not fail, and the POST is not sent again.
        await h.Link(); var posts = 0; var hospital = h.Hospital(availability: Agenda());
        var hospitalReads = new HttpMessageInvoker(hospital);
        var unreadable = new AgentHarness.Fake(request =>
        {
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != "/v1/agenda") return hospitalReads.SendAsync(request, CancellationToken.None);
            posts++; return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(answer, System.Text.Encoding.UTF8, "application/json") });
        });
        await h.Runtime(h.Model(AgentHarness.ToolCall("propose_action", new { action = "create", doctorId = doctor, startsAt = In(5, 15), durationMinutes = 30 }), AgentHarness.Reply("Listo.")), h.Sender(), unreadable).Run(h.Job, CancellationToken.None);
        await h.Db.Jobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "done"));
        await h.Say("patient", "CONFIRMAR " + Assert.Single(await h.Pending("proposal")).Code);
        var sender = h.Sender();

        await AgentWorker.RunTenant(h.Worker(h.Runtime(NoModel, sender, unreadable), sender), h.Scope.Id, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, posts);
        Assert.Equal("done", await h.Db.Jobs.AsNoTracking().Where(j => j.Id == h.Job.Id).Select(j => j.Status).SingleAsync());
        Assert.Equal("human", (await h.Fresh()).Status);
        Assert.Equal("La operación de agenda necesita conciliación. Comprueba el hospital antes de repetirla.", (await h.Db.Activities.AsNoTracking().SingleAsync(a => a.ConversationId == h.Conversation.Id && a.Kind == "handoff")).Body);
        Assert.DoesNotContain(h.Sent, text => text.Contains("quedó agendada"));
    }

    [Fact]
    public async Task AProposalThatCannotBeReadBeforeWritingIsNotCalledAReconciliation()
    {
        // Nothing reached Hospital: the team is told so, not asked to reconcile an operation that never happened.
        await h.Link(); var hospital = h.Hospital(availability: Agenda());
        await h.Runtime(h.Model(AgentHarness.ToolCall("propose_action", new { action = "create", doctorId = doctor, startsAt = In(5, 15), durationMinutes = 30 }), AgentHarness.Reply("Listo.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        var card = Assert.Single(await h.Pending("proposal")); card.Body = """{"action":"create"}"""; await h.Db.SaveChangesAsync();
        await h.Db.Jobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "done"));
        await h.Say("patient", "CONFIRMAR " + card.Code); var sender = h.Sender();

        await AgentWorker.RunTenant(h.Worker(h.Runtime(NoModel, sender, hospital), sender), h.Scope.Id, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

        Assert.Empty(h.HospitalWrites);
        Assert.Equal("done", await h.Db.Jobs.AsNoTracking().Where(j => j.Id == h.Job.Id).Select(j => j.Status).SingleAsync());
        Assert.Equal("No se pudo preparar la operación de agenda; no se envió nada a Hospital.", (await h.Db.Activities.AsNoTracking().SingleAsync(a => a.ConversationId == h.Conversation.Id && a.Kind == "handoff")).Body);
    }

    [Fact]
    public async Task AWriteWithNoAnswerNamesTheAppointmentToReconcile()
    {
        await h.Link(); var appointment = Guid.NewGuid(); var inner = new HttpMessageInvoker(h.Hospital(appointments: [Booked(appointment, 3)])); var posts = 0;
        var hospital = new AgentHarness.Fake(request =>
        {
            if (request.Method != HttpMethod.Post) return inner.SendAsync(request, CancellationToken.None);
            posts++; throw new HttpRequestException("Synthetic timeout");
        });
        await Say($"CANCELAR {appointment}", hospital);

        await Say(Options()[0].Id, hospital);

        Assert.Equal(1, posts);
        Assert.Equal($"La operación de agenda necesita conciliación. Comprueba el hospital antes de repetirla. Referencia Hospital: {appointment}.", (await h.Db.Activities.AsNoTracking().SingleAsync(a => a.ConversationId == h.Conversation.Id && a.Kind == "handoff")).Body);
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
