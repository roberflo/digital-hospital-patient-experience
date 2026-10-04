using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>docs/reception-agent.md, criterios 16–20: a new client registers over WhatsApp before
/// booking, and every handoff carries the hospital's emergency number. Synthetic people only.</summary>
public sealed class AgentIntakeTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();

    static readonly object Adult = new { givenNames = "Ana Sintética", familyNames = "López Prueba", birthDate = "1990-03-12", sex = "female", emergencyContactName = "Carlos Sintético", emergencyContactRelationship = "hermano", emergencyContactPhone = "70000001" };

    /// <summary>Hospital double for registration: records each POST /v1/patients body and answers with <paramref name="answer"/>.</summary>
    AgentHarness.Fake Registry(List<JsonElement> posts, Func<Guid, object> answer, HttpStatusCode status = HttpStatusCode.Created)
    {
        var created = Guid.NewGuid();
        return new(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/patients" && request.Method == HttpMethod.Post)
            {
                posts.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone());
                var response = AgentHarness.Json(answer(created)); response.StatusCode = status; return response;
            }
            if (path == $"/v1/patients/{created}") return AgentHarness.Json(new { patientId = created, givenNames = "Ana Sintética", familyNames = "López Prueba", phone = h.Contact.Phone });
            if (path == "/v1/agenda/booking-options") return AgentHarness.Json(new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = Array.Empty<object>() });
            throw new InvalidOperationException("Unexpected hospital request " + path);
        });
    }
    async Task<string> ProposeAndGetCode(HttpMessageHandler hospital, object arguments)
    {
        await h.Runtime(h.Model(AgentHarness.ToolCall("propose_registration", arguments), AgentHarness.Reply("Revisa tus datos y confirma.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        return (await h.Db.Activities.SingleAsync(a => a.ConversationId == h.Conversation.Id && a.Kind.StartsWith("proposal:"))).Kind[9..];
    }

    [Fact]
    public async Task NewClientIsRegisteredOnlyAfterConfirmingAndCanThenBook()
    {
        var posts = new List<JsonElement>(); var hospital = Registry(posts, id => new { created = true, patientId = id, duplicateCandidates = Array.Empty<object>() });

        var code = await ProposeAndGetCode(hospital, Adult);
        Assert.Empty(posts); // nothing reaches Hospital before the patient confirms
        Assert.Equal("CONFIRMAR " + code, h.Interactive[^1].GetProperty("action").GetProperty("buttons")[0].GetProperty("reply").GetProperty("id").GetString());

        await h.Say("patient", "CONFIRMAR " + code);
        await h.Runtime(h.Model(AgentHarness.Reply("No debe consultarse el modelo")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        var post = Assert.Single(posts);
        Assert.Equal("Ana Sintética", post.GetProperty("givenNames").GetString());
        Assert.Equal("1990-03-12", post.GetProperty("birthDate").GetString());
        Assert.Equal("female", post.GetProperty("sex").GetString());
        Assert.Equal(h.Contact.Phone, post.GetProperty("phone").GetString()); // the verified WhatsApp number, never an argument
        Assert.False(post.GetProperty("forceCreateDespiteDuplicate").GetBoolean());
        Assert.Equal("Carlos Sintético", post.GetProperty("emergencyContacts")[0].GetProperty("fullName").GetString());
        await h.Db.Entry(h.Contact).ReloadAsync();
        Assert.NotNull(h.Contact.PatientId);
        Assert.Equal("agent", (await h.Fresh()).Status);
        // The confirmed proposal keeps no personal data behind.
        var used = await h.Db.Activities.SingleAsync(a => a.ConversationId == h.Conversation.Id && a.Kind.StartsWith("proposal_used:"));
        Assert.DoesNotContain("1990", used.Body); Assert.DoesNotContain("Sintética", used.Body);
        Assert.Contains(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).ToListAsync(), a => a.Kind == "patient_registered");
    }

    [Fact]
    public async Task FirstAppointmentOfARegisteredClientIsAFirstVisit()
    {
        var patient = Guid.NewGuid(); var doctor = Guid.NewGuid(); var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero); JsonElement? booking = null;
        var hospital = new AgentHarness.Fake(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/patients") return AgentHarness.Json(new { created = true, patientId = patient, duplicateCandidates = Array.Empty<object>() });
            if (path.StartsWith("/v1/patients/")) return AgentHarness.Json(new { patientId = patient, givenNames = "Ana Sintética", familyNames = "López Prueba", phone = h.Contact.Phone });
            if (path == "/v1/agenda/booking-options") return AgentHarness.Json(new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = doctor, clinicianName = "Dra. Sintética", placeName = "", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = start, durationMinutes = 30, takenBy = 0, offered = true } } } } } } });
            booking = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            return AgentHarness.Json(new { appointmentId = Guid.NewGuid(), status = "booked", overlaps = false });
        });
        async Task Confirm() { var code = (await h.Db.Activities.SingleAsync(a => a.ConversationId == h.Conversation.Id && a.Kind.StartsWith("proposal:"))).Kind[9..]; await h.Say("patient", "CONFIRMAR " + code); await h.Runtime(h.Model(AgentHarness.Reply("No debe consultarse el modelo")), h.Sender(), hospital).Run(h.Job, CancellationToken.None); }

        await h.Runtime(h.Model(AgentHarness.ToolCall("propose_registration", Adult), AgentHarness.Reply("Confirma tu registro.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        await Confirm();
        await h.Say("patient", "Quiero la primera cita.");
        await h.Runtime(h.Model(AgentHarness.ToolCall("propose_action", new { action = "create", doctorId = doctor, startsAt = start, durationMinutes = 30 }), AgentHarness.Reply("Confirma tu cita.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        await Confirm();

        Assert.Equal("first-visit", Assert.NotNull(booking).GetProperty("visitKind").GetString());
        Assert.Equal(patient, booking.Value.GetProperty("patientId").GetGuid());
    }

    [Fact]
    public async Task PossibleDuplicateIsNeverForcedAndReceptionIsOffered()
    {
        var posts = new List<JsonElement>();
        var hospital = Registry(posts, _ => new { created = false, duplicateCandidates = new[] { new { patientId = Guid.NewGuid(), displayName = "Ana S. L.", birthDate = "1990-03-12", matchedOn = "name-and-birth-date" } } }, HttpStatusCode.OK);

        var code = await ProposeAndGetCode(hospital, Adult);
        await h.Say("patient", "CONFIRMAR " + code);
        await h.Runtime(h.Model(AgentHarness.Reply("No debe consultarse el modelo")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        Assert.False(Assert.Single(posts).GetProperty("forceCreateDespiteDuplicate").GetBoolean());
        await h.Db.Entry(h.Contact).ReloadAsync();
        Assert.Null(h.Contact.PatientId);
        Assert.Equal("agent", (await h.Fresh()).Status);
        Assert.Contains(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).ToListAsync(), a => a.Kind == "handoff_offer"); // the patient is asked, with a button, whether to go to a person
    }

    [Theory]
    [InlineData("2015-06-01", "female", "70000001")] // a minor needs a legal guardian: reception handles it
    [InlineData("1990-03-12", "otro", "70000001")]
    [InlineData("1990-03-12", "female", "")]
    [InlineData("no es una fecha", "female", "70000001")]
    public async Task InvalidRegistrationIsNeverProposed(string birthDate, string sex, string emergencyPhone)
    {
        var arguments = new { givenNames = "Ana Sintética", familyNames = "López Prueba", birthDate, sex, emergencyContactName = "Carlos Sintético", emergencyContactRelationship = "hermano", emergencyContactPhone = emergencyPhone };

        await h.Runtime(h.Model(AgentHarness.ToolCall("propose_registration", arguments), AgentHarness.Reply("No pude registrar esos datos.")), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Empty(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id && a.Kind.StartsWith("proposal:")).ToListAsync());
        Assert.DoesNotContain(h.Sent, text => text.Contains("CONFIRMAR"));
    }

    [Fact]
    public async Task LinkedPatientIsNeverRegisteredAgain()
    {
        await h.Link();

        await h.Runtime(h.Model(AgentHarness.ToolCall("propose_registration", Adult), AgentHarness.Reply("Ya tienes expediente.")), h.Sender(), h.Hospital()).Run(h.Job, CancellationToken.None);

        Assert.Empty(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id && a.Kind.StartsWith("proposal:")).ToListAsync());
    }

    [Theory]
    [InlineData("2200 0000", true)]
    [InlineData(null, false)]
    public async Task HandoffGivesTheHospitalsEmergencyNumber(string? phone, bool expected)
    {
        var tenant = await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id); tenant.EmergencyPhone = phone; await h.Db.SaveChangesAsync();
        await h.Say("patient", "Creo que tomé una sobredosis de mis pastillas.");

        await h.Runtime(h.Model(AgentHarness.Reply("No debe consultarse el modelo")), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("human", (await h.Fresh()).Status);
        var message = Assert.Single(h.Sent);
        Assert.Contains("emergencia", message);
        Assert.Equal(expected, message.Contains("2200 0000"));
    }
}
