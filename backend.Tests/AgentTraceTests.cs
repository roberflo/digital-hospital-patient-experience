using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>docs/reception-agent.md, criterios 47–51: what the agent does can be followed afterwards, in the CRM's
/// history and in Hospital's audit, and the patient is treated by name.</summary>
public sealed class AgentTraceTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    static readonly AgentHarness.Fake NoModel = new(_ => throw new InvalidOperationException("The model must not be consulted"));
    Task<List<Activity>> Trail() => h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).OrderBy(a => a.CreatedAt).ToListAsync();
    async Task Say(string text, HttpMessageHandler? hospital = null) { await h.Say("patient", text); await h.Runtime(NoModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None); }

    [Fact]
    public async Task BookingIsTraceableInTheCrmAndInHospital()
    {
        await h.Link();
        var doctor = Guid.NewGuid(); var appointment = Guid.NewGuid(); var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero); var callers = new List<string>();
        var hospital = new AgentHarness.Fake(request =>
        {
            callers.Add(request.Headers.UserAgent.ToString());
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/v1/patients/")) return Task.FromResult(AgentHarness.Json(new { patientId = h.Contact.PatientId, phone = h.Contact.Phone }));
            if (path == "/v1/agenda/booking-options") return Task.FromResult(AgentHarness.Json(new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = doctor, clinicianName = "Dra. Sintética Rivas", placeName = "Consultorio 1", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = start, durationMinutes = 30, takenBy = 0, offered = true } } } } } } }));
            return Task.FromResult(AgentHarness.Json(new { appointmentId = appointment, status = "booked", overlaps = false }));
        });

        await Say($"CITA {start.ToOffset(TimeSpan.FromHours(-6)):yyyy-MM-ddTHH:mm:sszzz} {doctor} 30 Dra. Sintética Rivas", hospital);
        await Say("Sí", hospital);

        // CRM: what was booked, with whom, and the reference that finds it in Hospital.
        var booked = (await Trail()).Single(a => a.Kind == "appointment").Body;
        Assert.Contains("a las 09:00", booked); Assert.Contains("Dra. Sintética Rivas", booked); Assert.Contains(appointment.ToString(), booked);
        Assert.Contains(await Trail(), a => a.Kind == "agent_tool" && a.Body.StartsWith("Herramienta: propose_action."));
        // Hospital: every call says it came from the WhatsApp agent and from which conversation (Hospital stores the User-Agent as origin_agent).
        Assert.NotEmpty(callers);
        Assert.All(callers, caller => { Assert.Contains("Recepcion-AgenteWhatsApp", caller); Assert.Contains(h.Conversation.Id.ToString(), caller); });
    }

    [Fact]
    public async Task MenuActionsLeaveTheSameTrailAsTheAgentsOwn()
    {
        await h.Link();
        var prescription = Guid.NewGuid();
        var hospital = h.Hospital(new { prescriptionId = prescription, patientId = h.Contact.PatientId, encounterId = Guid.NewGuid(), state = "signed", signedAt = DateTimeOffset.UtcNow, contentWithheld = false, lines = Array.Empty<object>() },
            availability: new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = Array.Empty<object>() });

        await Say("RECETA", hospital);
        await Say("AGENDAR", hospital);

        var trail = await Trail();
        Assert.Contains(prescription.ToString(), trail.Single(a => a.Kind == "prescription_delivered").Body); // which prescription went out
        Assert.Contains(trail, a => a.Kind == "agent_tool" && a.Body.StartsWith("Herramienta: hospital_availability."));
    }

    [Fact]
    public async Task RegistrationFormInProgressNeverShowsInTheHistory()
    {
        var hour = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero).ToOffset(TimeSpan.FromHours(-6)).ToString("yyyy-MM-ddTHH:mm:sszzz");
        await Say($"CITA {hour} {Guid.NewGuid()} 30 Dra. Sintética Rivas");
        Assert.Contains(hour, Assert.Single(await Trail(), a => a.Kind == "intake").Body); // the form is open and holds the hour
        await Say("Ana Sintética"); await Say("López Prueba");

        var feed = ((Microsoft.AspNetCore.Http.HttpResults.Ok<ActivityFeedPage>)await ActivityFeed.Read(h.Db, h.Scope, null, null, null, null, h.Conversation.Id, null, null, 1, CancellationToken.None)).Value!;
        Assert.DoesNotContain(feed.Items, item => item.Kind.StartsWith("intake") || item.Body.Contains("Sintética") || item.Body.Contains("givenNames"));
        Assert.DoesNotContain(feed.Items, item => item.Body.Contains(hour[..16])); // nor the hour chosen with it (agent-slot-first INV-T-5)
        Assert.Contains(feed.Items, item => item.Body == "Inicio del registro guiado: completado."); // that it started is history; what was typed is not
    }

    [Fact]
    public async Task RegisteringNamesTheContactAndPointsToTheHospitalRecord()
    {
        var patient = Guid.NewGuid();
        var hospital = new AgentHarness.Fake(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/patients") return Task.FromResult(AgentHarness.Json(new { created = true, patientId = patient, duplicateCandidates = Array.Empty<object>() }));
            if (path.StartsWith("/v1/patients/")) return Task.FromResult(AgentHarness.Json(new { patientId = patient, givenNames = "Ana Sintética", familyNames = "López Prueba", phone = h.Contact.Phone }));
            return Task.FromResult(AgentHarness.Json(new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = Array.Empty<object>() }));
        });
        // Registering without having chosen an hour: the agent opens the form, the rest needs no model.
        await h.Runtime(h.Model(AgentHarness.ToolCall("start_registration", new { }), AgentHarness.Reply("Ok")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        Assert.Single(await Trail(), a => a.Kind == "intake");
        foreach (var text in new[] { "Ana Sintética", "López Prueba", "12/03/1990", "Femenino", "Carlos Sintético", "Hermano", "7000 0001", "Sí" }) await Say(text, hospital);

        await h.Db.Entry(h.Contact).ReloadAsync();
        Assert.Equal("Ana Sintética López Prueba", h.Contact.Name); // the CRM shows who the person said they are, not the WhatsApp profile name
        Assert.Contains(patient.ToString(), (await Trail()).Single(a => a.Kind == "patient_registered").Body);
        Assert.StartsWith("Listo, Ana", h.Sent[^1]);
        Assert.DoesNotContain(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).ToListAsync(), a => a.Kind.StartsWith("intake")); // the finished form is gone, not archived
    }

    [Theory]
    [InlineData("Ana Sintética", "¡Hola, Ana!")]
    [InlineData("Paciente", "¡Hola!")]            // the placeholder the webhook uses when WhatsApp gives no name
    [InlineData("🔥 Promo 24/7 🔥", "¡Hola!")]    // a profile name that is not a person's name
    public async Task GreetingUsesTheNameOnlyWhenItIsOne(string contactName, string opening)
    {
        h.Contact.Name = contactName; await h.Db.SaveChangesAsync();

        await Say("Hola");

        Assert.StartsWith(opening, h.Sent[^1]);
    }

    [Fact]
    public async Task TheFormThanksByName()
    {
        await Say($"CITA {new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero).ToOffset(TimeSpan.FromHours(-6)):yyyy-MM-ddTHH:mm:sszzz} {Guid.NewGuid()} 30 Dra. Sintética Rivas");
        Assert.Single(await Trail(), a => a.Kind == "intake");
        await Say("Ana Sintética");

        Assert.StartsWith("Gracias, Ana.", h.Sent[^1]);
    }
}
