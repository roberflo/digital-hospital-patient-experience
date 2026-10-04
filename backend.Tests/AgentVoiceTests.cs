using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>docs/reception-agent.md, criterios 39–43: what the patient reads. Short lines with room between ideas,
/// the datum that matters in bold, no machine instructions when a button does the job.</summary>
public sealed class AgentVoiceTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    static readonly AgentHarness.Fake NoModel = new(_ => throw new InvalidOperationException("The model must not be consulted"));
    static void ReadsLikeAChat(string message) => Assert.All(message.Split('\n'), line => Assert.True(line.Length <= 130, $"A line of {line.Length} characters reads as a paragraph: «{line}»"));
    AgentHarness.Fake Booking(Guid doctor, DateTimeOffset start, List<string> writes) => new(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.StartsWith("/v1/patients/")) return Task.FromResult(AgentHarness.Json(new { patientId = h.Contact.PatientId, phone = h.Contact.Phone }));
        if (path == "/v1/agenda/booking-options") return Task.FromResult(AgentHarness.Json(new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = doctor, clinicianName = "Dra. Sintética Rivas", placeName = "Consultorio 1", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = start, durationMinutes = 30, takenBy = 0, offered = true } } } } } } }));
        writes.Add(path); return Task.FromResult(AgentHarness.Json(new { appointmentId = Guid.NewGuid(), status = "booked", overlaps = false }));
    });
    static string Slot(DateTimeOffset start, Guid doctor) => $"CITA {start.ToOffset(TimeSpan.FromHours(-6)):yyyy-MM-ddTHH:mm:sszzz} {doctor} 30 Dra. Sintética Rivas";

    [Fact]
    public async Task ProposalIsACardWithAQuestionNotAnInstructionManual()
    {
        await h.Link();
        var doctor = Guid.NewGuid(); var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero);
        await h.Say("patient", Slot(start, doctor));

        await h.Runtime(NoModel, h.Sender(), Booking(doctor, start, [])).Run(h.Job, CancellationToken.None);

        var sent = Assert.Single(h.Sent);
        ReadsLikeAChat(sent);
        Assert.Contains("*Cita:*", sent); Assert.Contains("a las 09:00", sent); Assert.Contains("Dra. Sintética Rivas", sent);
        Assert.EndsWith("¿La confirmo?", sent);
        Assert.DoesNotContain("CONFIRMAR", sent); // the button carries the code; the patient is not asked to type it
        Assert.StartsWith("CONFIRMAR ", h.Interactive[^1].GetProperty("action").GetProperty("buttons")[0].GetProperty("reply").GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("Sí")]
    [InlineData("si, confírmala")]
    [InlineData("Ok, dale")]
    public async Task SayingYesToTheProposalJustMadeConfirmsIt(string answer)
    {
        await h.Link();
        var doctor = Guid.NewGuid(); var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero); var writes = new List<string>();
        var hospital = Booking(doctor, start, writes);
        await h.Say("patient", Slot(start, doctor)); await h.Runtime(NoModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        Assert.Empty(writes);

        await h.Say("patient", answer); await h.Runtime(NoModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        Assert.Equal("/v1/agenda", Assert.Single(writes));
        ReadsLikeAChat(h.Sent[^1]); Assert.Contains("quedó agendada", h.Sent[^1]); Assert.Contains("a las 09:00", h.Sent[^1]);
    }

    [Fact]
    public async Task YesWithNothingProposedIsAnOrdinaryMessage()
    {
        await h.Link(); await h.Say("patient", "Sí");
        var model = h.Model(AgentHarness.Reply("¿En qué te ayudo?"));

        await h.Runtime(model, h.Sender(), h.Hospital()).Run(h.Job, CancellationToken.None);

        Assert.Equal(1, model.Calls);
        Assert.Empty(h.HospitalWrites);
    }

    [Fact]
    public async Task ProposalDoesNotCarryTheEmergencyQuestion()
    {
        await h.Link();
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1).AddHours(15), TimeSpan.Zero);
        var hospital = h.Hospital(availability: new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = Guid.NewGuid(), clinicianName = "Dra. Sintética", placeName = "", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = DateTimeOffset.UtcNow.AddHours(30), durationMinutes = 30, takenBy = 0, offered = true } } } } } } });
        var model = h.Model(AgentHarness.ToolCall("hospital_availability", new { date = DateTime.UtcNow.AddHours(-6).ToString("yyyy-MM-dd") }), AgentHarness.ToolCall("propose_action", new { action = "create", doctorId = Guid.NewGuid(), startsAt = start, durationMinutes = 30 }), AgentHarness.Reply("Te propongo esta cita."));

        await h.Runtime(model, h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        var sent = Assert.Single(h.Sent);
        ReadsLikeAChat(sent);
        Assert.DoesNotContain("emergencia", sent); // one question at a time: «¿la confirmo?» and «¿es una emergencia?» answered with one «sí» would be ambiguous
        Assert.DoesNotContain("CONFIRMAR", sent);
    }

    [Theory]
    [InlineData("Creo que tomé una sobredosis")]
    [InlineData("Quiero hablar con una persona, por favor")]
    public async Task HandoffIsTwoShortParagraphs(string message)
    {
        var tenant = await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id); tenant.EmergencyPhone = "2200 0000"; await h.Db.SaveChangesAsync();
        await h.Say("patient", message);

        await h.Runtime(NoModel, h.Sender()).Run(h.Job, CancellationToken.None);

        var sent = Assert.Single(h.Sent);
        ReadsLikeAChat(sent);
        Assert.Contains("\n\n", sent); Assert.Contains("*2200 0000*", sent);
    }

    [Fact]
    public async Task ModelMarkdownBecomesWhatsAppBoldAndListedHoursAreNotRepeatedByTheSystem()
    {
        await h.Runtime(h.Model(AgentHarness.Reply("Tu cita es el **lunes**.\n\n¿Algo más?")), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("Tu cita es el *lunes*.\n\n¿Algo más?", Assert.Single(h.Sent));
    }

    [Fact]
    public async Task AgentStartsTheGuidedFormInsteadOfListingEverythingItNeeds()
    {
        // The real conversation: «necesito nombres, apellidos, fecha de nacimiento, sexo registral, contacto…» in one 646-character block.
        var model = h.Model(AgentHarness.ToolCall("start_registration", new { }), AgentHarness.Reply("Para registrarte necesito varios datos: nombres, apellidos, fecha de nacimiento, sexo registral y un contacto de emergencia con nombre, parentesco y teléfono."));

        await h.Runtime(model, h.Sender()).Run(h.Job, CancellationToken.None);

        var sent = Assert.Single(h.Sent);
        ReadsLikeAChat(sent);
        Assert.Contains("Paso 1 de 7", sent);
        Assert.DoesNotContain("parentesco", sent); // one question at a time
        Assert.Single(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id && a.Kind == "intake").ToListAsync());
    }

    [Fact]
    public async Task EveryStepOfRegisteringReadsLikeAChat()
    {
        await h.Say("patient", "AGENDAR"); await h.Runtime(NoModel, h.Sender()).Run(h.Job, CancellationToken.None);
        foreach (var answer in new[] { "Ana Sintética", "López Prueba", "12/03/1990", "Femenino", "Carlos Sintético", "Hermano", "7000 0001" }) { await h.Say("patient", answer); await h.Runtime(NoModel, h.Sender()).Run(h.Job, CancellationToken.None); }

        Assert.All(h.Sent, ReadsLikeAChat);
        var card = h.Sent[^1];
        Assert.Contains("*Ana Sintética López Prueba*", card); Assert.Contains("\nNacimiento: 12 de marzo de 1990", card); Assert.Contains("\nContacto de emergencia: Carlos Sintético (Hermano), 70000001", card);
        Assert.EndsWith("¿Están correctos?", card); Assert.DoesNotContain("CONFIRMAR", card);
    }

    [Theory]
    [InlineData("create", "*Cita:*", "¿La confirmo?")]
    [InlineData("reschedule", "*Nueva fecha:*", "¿La cambio?")]
    [InlineData("cancel", "*Cancelar cita:*", "¿La cancelo?")]
    public async Task AProposalReachesThePatientAsTheServersCardAndNothingElse(string action, string label, string question)
    {
        // Read in the evals: the model repeated the date above the card, and a cancellation never said which appointment.
        await h.Link();
        var doctor = Guid.NewGuid(); var appointment = Guid.NewGuid(); var booked = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(2).AddHours(16), TimeSpan.Zero); var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero);
        var hospital = h.Hospital(appointments: [new { appointmentId = appointment, clinicianId = doctor, clinicianName = "Dra. Sintética Rivas", scheduledStart = booked, durationMinutes = 30, status = "booked" }],
            availability: new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = doctor, clinicianName = "Dra. Sintética Rivas", placeName = "Consultorio 1", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = start, durationMinutes = 30, takenBy = 0, offered = true } } } } } } });
        var model = h.Model(AgentHarness.ToolCall("hospital_availability", new { date = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd") }),
            AgentHarness.ToolCall("propose_action", new { action, appointmentId = appointment, doctorId = doctor, startsAt = start, durationMinutes = 30 }),
            AgentHarness.Reply("Listo, tu cita ya quedó agendada con la doctora Rivas el día que pediste a la hora que pediste, te espero."));

        await h.Runtime(model, h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        var sent = Assert.Single(h.Sent);
        ReadsLikeAChat(sent);
        Assert.StartsWith(label, sent); Assert.EndsWith(question, sent);
        Assert.Contains("*Con:* Dra. Sintética Rivas", sent);
        Assert.Contains(action == "cancel" ? "a las 10:00" : "a las 09:00", sent); // a cancellation names the appointment being cancelled
        Assert.DoesNotContain("quedó agendada", sent); // what the model wrote about a proposal is never sent, so it cannot claim it is done
        Assert.Equal("agent", (await h.Fresh()).Status);
        Assert.Empty(h.HospitalWrites);
    }
}
