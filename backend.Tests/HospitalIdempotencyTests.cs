using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Recepcion.Integrations;
using Xunit;

/// <summary>MR-41: Recepción sends Hospital an <c>Idempotency-Key</c> on every create (POST v1/agenda, POST v1/patients), so a write that
/// timed out and is sent again is answered with the original instead of a second appointment or record. The agent keys by the proposal the
/// patient confirmed («agent:», «agent-register:»), never by the turn; the team by the key its browser already sends («staff:»).
/// A key Hospital already holds with other data (409 *.idempotency_key_reused) is an uncertain result for a person to reconcile, never a retry.</summary>
public sealed class HospitalIdempotencyTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    static readonly AgentHarness.Fake NoModel = new(_ => throw new InvalidOperationException("The model must not be consulted"));
    static readonly Guid Doctor = Guid.NewGuid();
    static readonly DateTimeOffset Start = new(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero);
    static object Agenda() => new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = Doctor, clinicianName = "Dra. Sintética", placeName = "", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = Start, durationMinutes = 30, takenBy = 0, offered = true } } } } } } };
    static readonly object Adult = new { givenNames = "Ana Sintética", familyNames = "López Prueba", birthDate = "1990-03-12", sex = "female", emergencyContactName = "Carlos Sintético", emergencyContactRelationship = "hermano", emergencyContactPhone = "70000001" };

    /// <summary>Every write sent to Hospital, with the Idempotency-Key it carried (null when it carried none).</summary>
    readonly List<(string Path, string? Key)> writes = [];
    /// <summary>The harness's Hospital, which also registers adults; <paramref name="refuse"/> answers any write (or throws) instead of it.</summary>
    AgentHarness.Fake Hospital(Func<HttpRequestMessage, HttpResponseMessage?>? refuse = null)
    {
        var inner = new HttpMessageInvoker(h.Hospital(availability: Agenda())); var created = Guid.NewGuid();
        return new(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                if (path is "/v1/agenda" or "/v1/patients") lock (writes) writes.Add((path, request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null));
                if (refuse?.Invoke(request) is { } refusal) return refusal;
                if (path == "/v1/patients") { var registered = AgentHarness.Json(new { created = true, patientId = created, duplicateCandidates = Array.Empty<object>() }); registered.StatusCode = HttpStatusCode.Created; return registered; }
            }
            if (path == $"/v1/patients/{created}") return AgentHarness.Json(new { patientId = created, givenNames = "Ana Sintética", familyNames = "López Prueba", phone = h.Contact.Phone });
            return await inner.SendAsync(request, CancellationToken.None);
        });
    }
    /// <summary>Hospital's answer to a key it already holds with another body (ResultExtensions: a problem with its «code»).</summary>
    static HttpResponseMessage Reused(string code) => new(HttpStatusCode.Conflict) { Content = new StringContent(JsonSerializer.Serialize(new { title = "Conflict", status = 409, code }), Encoding.UTF8, "application/problem+json") };

    async Task<PendingAction> Propose(HttpMessageHandler hospital, string tool, object arguments)
    {
        if (tool == "propose_registration") await h.Say("patient", "Soy Ana Sintética López Prueba, mujer, nací el 12/03/1990. Mi hermano Carlos Sintético, 70000001."); // what the patient wrote of Adult: only that reaches a registration (docs/agent-evidence.md)
        await h.Runtime(h.Model(AgentHarness.ToolCall(tool, arguments), AgentHarness.Reply("Confirma, por favor.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        return Assert.Single(await h.Pending("proposal"));
    }
    async Task Confirm(PendingAction proposal, HttpMessageHandler hospital)
    {
        await h.Say("patient", "CONFIRMAR " + proposal.Code);
        await h.Runtime(NoModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None);
    }
    Task<PendingAction> ProposeAppointment(HttpMessageHandler hospital) => Propose(hospital, "propose_action", new { action = "create", doctorId = Doctor, startsAt = Start, durationMinutes = 30 });

    [Fact]
    public async Task TheAgentKeysTheAppointmentByTheProposalThePatientConfirmed()
    {
        await h.Link(); var hospital = Hospital();
        var proposal = await ProposeAppointment(hospital);

        await Confirm(proposal, hospital);

        Assert.Equal(("/v1/agenda", "agent:" + proposal.Id), Assert.Single(writes));
    }

    [Fact]
    public async Task ConfirmingTheSameProposalAgainSendsTheSameKey()
    {
        // Simulates a resend that production prevents: Confirm consumes the proposal before writing, so the same proposal is never confirmed
        // twice. Reopening it here only shows that the key comes from the proposal, not from the turn or the job that runs it.
        await h.Link(); var hospital = Hospital();
        var proposal = await ProposeAppointment(hospital); var body = proposal.Body;
        await Confirm(proposal, hospital);
        proposal.ConsumedAt = null; proposal.Body = body; await h.Db.SaveChangesAsync();

        await Confirm(proposal, hospital);

        Assert.Equal(2, writes.Count);
        Assert.All(writes, write => Assert.Equal("agent:" + proposal.Id, write.Key));
    }

    [Fact]
    public async Task TheAgentKeysTheRegistrationByTheSameProposal()
    {
        var hospital = Hospital();
        var proposal = await Propose(hospital, "propose_registration", Adult);

        await Confirm(proposal, hospital);

        Assert.Equal(("/v1/patients", "agent-register:" + proposal.Id), Assert.Single(writes));
        await h.Db.Entry(h.Contact).ReloadAsync(); Assert.NotNull(h.Contact.PatientId);
    }

    [Fact]
    public async Task ARegistrationWithItsHourKeysEachWriteByTheSameProposal()
    {
        var hospital = Hospital();
        var proposal = await Propose(hospital, "propose_registration", Adult);
        // The hour tapped before the form, with the keys ProposeRegistration stores it under: one card, one Confirmar.
        var card = System.Text.Json.Nodes.JsonNode.Parse(proposal.Body)!.AsObject();
        card["startsAt"] = Start.ToString("O"); card["doctorId"] = Doctor.ToString(); card["durationMinutes"] = 30;
        proposal.Body = card.ToJsonString(); await h.Db.SaveChangesAsync();

        await Confirm(proposal, hospital);

        Assert.Equal([("/v1/patients", "agent-register:" + proposal.Id), ("/v1/agenda", "agent:" + proposal.Id)], writes);
    }

    [Theory]
    [InlineData("/v1/agenda", "scheduling.idempotency_key_reused")]
    [InlineData("/v1/patients", "patient.idempotency_key_reused")]
    public async Task AKeyHospitalHoldsWithOtherDataIsHandedOffForReconciliationNeverRetried(string route, string code)
    {
        if (route == "/v1/agenda") await h.Link();
        var hospital = Hospital(request => request.RequestUri!.AbsolutePath == route ? Reused(code) : null);
        var proposal = route == "/v1/agenda" ? await ProposeAppointment(hospital) : await Propose(hospital, "propose_registration", Adult);

        await Confirm(proposal, hospital);

        Assert.Single(writes); // the same write is never sent again, under this key or another
        var conversation = await h.Fresh();
        Assert.Equal("human", conversation.Status);
        Assert.Contains("conciliación", conversation.Summary);
    }

    /// <summary>A request of a member of the team, with their own token (MR-11) and the key their browser sent.</summary>
    HttpContext Staff(string key)
    {
        var token = "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { tenant_id = h.Scope.Id, sub = "staff-1", exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic";
        var ctx = new DefaultHttpContext(); ctx.Request.Headers.Authorization = "Bearer " + token; ctx.Request.Headers["Idempotency-Key"] = key; return ctx;
    }
    Task<IResult> StaffBooks(HttpMessageHandler hospital, string key) =>
        HospitalEndpoints.ChangeAppointment(new AppointmentInput(h.Contact.Id, "create", null, Doctor, Start, 30), Staff(key), h.Db, h.Client(hospital), h.Scope, new CurrentUser { Subject = "staff-1", Name = "Recepción Sintética", Role = "agent" });

    [Fact]
    public async Task TheTeamKeysTheAppointmentByTheKeyItsBrowserSent()
    {
        await h.Link(); var key = Guid.NewGuid().ToString();

        await StaffBooks(Hospital(), key);

        Assert.Equal(("/v1/agenda", "staff:" + key), Assert.Single(writes));
    }

    [Fact]
    public async Task TheTeamIsToldToCheckTheAgendaWhenHospitalHoldsTheKeyWithOtherData()
    {
        await h.Link(); var key = Guid.NewGuid().ToString();

        var error = await Assert.ThrowsAsync<HospitalIntegrationException>(() => StaffBooks(Hospital(_ => Reused("scheduling.idempotency_key_reused")), key));

        Assert.Equal(("scheduling.idempotency_key_reused", HttpStatusCode.Conflict), (error.Code, error.StatusCode));
        Assert.Contains("Comprueba la agenda", error.DisplayMessage);
        Assert.Single(writes);
        // The result is uncertain: the key stays spent, so the same request is not sent again from here.
        Assert.True(await h.Db.Receipts.AnyAsync(r => r.Key == "appointment:" + key));
    }

    [Fact]
    public async Task AfterATimeoutTheTeamRepeatsTheCreateWithTheSameKeyAndGetsTheOriginal()
    {
        // N1 (coordinator's decision): a create that timed out frees its receipt, so repeating it with the same key reaches Hospital, which
        // answers the original appointment by its Idempotency-Key. No duplicate can come of it: Hospital deduplicates.
        await h.Link(); var key = Guid.NewGuid().ToString(); var original = Guid.NewGuid(); var timeout = true;
        var hospital = Hospital(request => request.RequestUri!.AbsolutePath != "/v1/agenda" ? null
            : timeout ? throw new TaskCanceledException("Synthetic Hospital timeout", new TimeoutException())
            : AgentHarness.Json(new { appointmentId = original, status = "booked", overlaps = false }));

        var error = await Assert.ThrowsAsync<HospitalIntegrationException>(() => StaffBooks(hospital, key));
        Assert.Equal("hospital.timeout", error.Code);
        timeout = false;
        var retried = await StaffBooks(hospital, key);

        Assert.Equal(StatusCodes.Status200OK, ((IStatusCodeHttpResult)retried).StatusCode);
        Assert.Equal(original, ((IValueHttpResult)retried).Value is HospitalAppointmentCreated created ? created.AppointmentId : Guid.Empty);
        Assert.Equal([("/v1/agenda", "staff:" + key), ("/v1/agenda", "staff:" + key)], writes);
    }

    [Theory]
    [InlineData("reschedule")]
    [InlineData("cancel")]
    public async Task OnlyACreateFreesItsKeyAfterATimeout(string action)
    {
        await h.Link(); var key = Guid.NewGuid().ToString();
        var hospital = Hospital(request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/" + action) ? throw new TaskCanceledException("Synthetic Hospital timeout", new TimeoutException()) : null);

        await Assert.ThrowsAsync<HospitalIntegrationException>(() => HospitalEndpoints.ChangeAppointment(new AppointmentInput(h.Contact.Id, action, Guid.NewGuid(), Doctor, Start, 30, action == "cancel" ? "patient-requested" : null), Staff(key), h.Db, h.Client(hospital), h.Scope, new CurrentUser { Subject = "staff-1", Name = "Recepción Sintética", Role = "agent" }));

        Assert.True(await h.Db.Receipts.AnyAsync(r => r.Key == "appointment:" + key));
    }

    [Fact]
    public async Task ABrowserKeyTooLongForTheStaffPrefixIsRefusedBeforeAnythingIsSpent()
    {
        await h.Link(); var key = new string('a', 95); // «staff:» + 95 would be 101, past Hospital's 100

        await Assert.ThrowsAsync<ArgumentException>(() => StaffBooks(Hospital(), key));

        Assert.Empty(writes);
        Assert.False(await h.Db.Receipts.AnyAsync(r => r.Key == "appointment:" + key));
    }

    [Theory]
    [InlineData("clave con espacios")]
    [InlineData("ñandú")]
    public async Task ABrowserKeyHospitalWouldRefuseIsRefusedBeforeAnythingIsSpent(string key)
    {
        await h.Link();

        await Assert.ThrowsAsync<ArgumentException>(() => StaffBooks(Hospital(), key));

        Assert.Empty(writes);
        Assert.False(await h.Db.Receipts.AnyAsync(r => r.Key == "appointment:" + key));
    }
}
