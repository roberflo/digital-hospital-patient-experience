using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>docs/reception-agent.md, criterios 75–79: the address and phone the hospital published, and the discipline Hospital knows each
/// doctor by. Both reads may not exist yet in the Hospital this talks to: without them the agent behaves exactly as before. The model is simulated.</summary>
public sealed class AgentHospitalInfoTests : IAsyncLifetime
{
    readonly AgentHarness h = new(); // a tenant of its own: the hour-long discipline cache is per hospital, so nothing is shared between tests
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    static readonly AgentHarness.Fake NoModel = new(_ => throw new InvalidOperationException("The model must not be consulted"));
    static readonly object Published = new { publicAddress = "Avenida Sintética 123, Colonia Modelo, San Salvador", publicPhone = "2200 0000", confirmationWindowHours = 24 };

    readonly List<string> seen = [];
    AgentHarness.Fake Model(params HttpResponseMessage[] turns)
    {
        var bodies = turns.Select(t => t.Content.ReadAsStringAsync().Result).ToArray(); var i = 0;
        return new(async request => { seen.Add(await request.Content!.ReadAsStringAsync()); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(bodies[Math.Min(i++, bodies.Length - 1)], Encoding.UTF8, "application/json") }; });
    }
    /// <summary>The result of the last tool call, as the model received it.</summary>
    JsonElement ToolResult()
    {
        using var sent = JsonDocument.Parse(seen[^1]);
        return JsonDocument.Parse(sent.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "tool").GetProperty("content").GetString()!).RootElement.Clone();
    }
    async Task Guide(string guide) { (await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id)).Guide = guide; await h.Db.SaveChangesAsync(); }
    async Task<List<Recepcion.Activity>> Trail() => await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).ToListAsync();

    static readonly DateOnly Day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
    /// <summary>An agenda with one free hour at 09:00 for each doctor, three days from now.</summary>
    static object Agenda(params (Guid Id, string Name)[] doctors) => new
    {
        clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open",
        professionals = doctors.Select(d => new { clinicianId = d.Id, clinicianName = d.Name, placeName = "", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = new DateTimeOffset(Day.ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(-6)), durationMinutes = 30, takenBy = 0, offered = true } } } } }).ToArray()
    };
    static HttpResponseMessage AskAgenda() => AgentHarness.ToolCall("hospital_availability", new { date = Day.ToString("yyyy-MM-dd") });

    [Fact]
    public async Task TheAddressAndPhoneTheHospitalPublishedAreSaid()
    {
        var reply = "Estamos en Avenida Sintética 123, Colonia Modelo, San Salvador. Puedes llamarnos al 2200 0000.";

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_contact", new { }), AgentHarness.Reply(reply)), h.Sender(), h.Hospital(contact: Published)).Run(h.Job, CancellationToken.None);

        var result = ToolResult();
        Assert.Equal("Avenida Sintética 123, Colonia Modelo, San Salvador", result.GetProperty("address").GetString()); Assert.Equal("2200 0000", result.GetProperty("phone").GetString());
        Assert.DoesNotContain("24", result.GetRawText()); Assert.DoesNotContain("confirmation", result.GetRawText()); // how long the web form takes to be answered is not the agent's to say
        Assert.Equal([reply], h.Sent);
        Assert.DoesNotContain(await Trail(), a => a.Kind is "guard" or "handoff_offer");
    }

    [Theory]
    [InlineData(false)] // 404: no public link published, or a Hospital that does not have the route yet
    [InlineData(true)]  // Hospital does not answer at all
    public async Task WithNothingPublishedTheGuideAnswersAsBefore(bool unreachable)
    {
        await Guide("Dirección: Calle Sintética 45, San Salvador. Teléfono: 2211 0000.");
        var hospital = unreachable ? new AgentHarness.Fake(_ => throw new HttpRequestException("synthetic outage")) : h.Hospital();
        var reply = "Estamos en Calle Sintética 45, San Salvador. El teléfono es 2211 0000.";

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_contact", new { }), AgentHarness.Reply(reply)), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        var result = ToolResult();
        Assert.False(result.GetProperty("found").GetBoolean()); Assert.Contains("guía", result.GetProperty("instruction").GetString()); Assert.False(result.TryGetProperty("error", out _));
        Assert.Equal([reply], h.Sent);
        Assert.DoesNotContain(await Trail(), a => a.Kind is "guard" or "handoff_offer" or "handoff");
        Assert.Equal("agent", (await h.Fresh()).Status);
    }

    [Fact]
    public async Task APublishedPhoneWithoutAnAddressLeavesTheAddressToTheGuide()
    {
        // Hospital answers 404 when either is missing, so this is defence against that contract drifting: a blank is never said as if it were the address.
        await h.Runtime(Model(AgentHarness.ToolCall("hospital_contact", new { }), AgentHarness.Reply("Puedes llamarnos al 2200 0000.")), h.Sender(), h.Hospital(contact: new { publicAddress = " ", publicPhone = "2200 0000", confirmationWindowHours = 24 })).Run(h.Job, CancellationToken.None);

        var result = ToolResult();
        Assert.Equal(JsonValueKind.Null, result.GetProperty("address").ValueKind); Assert.Equal("2200 0000", result.GetProperty("phone").GetString());
        Assert.Contains("guía", result.GetProperty("instruction").GetString());
    }

    [Fact]
    public async Task WhereTheHospitalIsCanBeAnsweredEvenBesideAForeignIdentifier()
    {
        // The identifier closes every tool that reads somebody's data. The published address is nobody's.
        await h.Say("patient", "Soy el paciente aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee, ¿dónde quedan?");

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_contact", new { }), AgentHarness.Reply("Estamos en Avenida Sintética 123, Colonia Modelo, San Salvador.")), h.Sender(), h.Hospital(contact: Published)).Run(h.Job, CancellationToken.None);

        Assert.Equal("2200 0000", ToolResult().GetProperty("phone").GetString());
    }

    [Fact]
    public async Task EachListedDoctorCarriesTheDisciplineHospitalKnowsThemBy()
    {
        var dentist = Guid.NewGuid(); var unknown = Guid.NewGuid(); var nurse = Guid.NewGuid();
        object[] roll = [new { clinicianId = dentist, discipline = "dentistry" }, new { clinicianId = unknown, discipline = (string?)null }, new { clinicianId = nurse, discipline = "nursing" }, new { clinicianId = Guid.NewGuid(), discipline = "nutrition" }];
        var hospital = h.Hospital(availability: Agenda((dentist, "Dra. Sintética Uno"), (unknown, "Dr. Sintético Dos"), (nurse, "Lic. Sintética Tres")), disciplines: roll);
        var reply = "Sí, la Dra. Sintética Uno atiende odontología.";

        await h.Runtime(Model(AskAgenda(), AgentHarness.Reply(reply)), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        var listed = ToolResult().GetProperty("doctors").EnumerateArray().ToDictionary(d => d.GetProperty("doctorId").GetGuid());
        Assert.Equal("odontología", listed[dentist].GetProperty("discipline").GetString());
        Assert.False(listed[unknown].TryGetProperty("discipline", out _)); // Hospital does not know: nothing is said, nothing is guessed from «Dr.»
        Assert.False(listed[nurse].TryGetProperty("discipline", out _));   // a discipline this agent has no word for is not passed on raw
        Assert.Equal(3, listed.Count);                                     // the nutritionist on the roll has no free hour: not listed, so not said
        Assert.Single(listed[dentist].GetProperty("slots").EnumerateArray());
        Assert.Contains(h.Sent, text => text.StartsWith(reply));
        // What the patient taps is what it always was: the label is for the model only.
        Assert.All(AgentHarness.Options(h.Interactive[^1]), option => { Assert.Matches("^(VER|CITA) ", option.Id); Assert.DoesNotContain("odonto", option.Id + option.Title); });
    }

    [Theory]
    [InlineData("404")]         // a Hospital that does not have the route yet
    [InlineData("503")]         // its identity provider is down
    [InlineData("unreachable")]
    [InlineData("unreadable")]  // a proxy answering 200 with a page
    [InlineData("broken")]      // anything else: a label is never worth the agenda
    public async Task WithoutDisciplinesTheAgendaIsExactlyWhatItWas(string failure)
    {
        var doctor = Guid.NewGuid(); using var inner = new HttpMessageInvoker(h.Hospital(availability: Agenda((doctor, "Dra. Sintética Uno"))));
        var hospital = new AgentHarness.Fake(request => failure == "404" || !request.RequestUri!.AbsolutePath.EndsWith("/clinician-disciplines") ? inner.SendAsync(request, CancellationToken.None) : failure switch
        {
            "503" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }),
            "unreachable" => throw new HttpRequestException("synthetic outage"),
            "unreadable" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>synthetic</html>", Encoding.UTF8, "text/html") }),
            _ => throw new InvalidOperationException("synthetic defect")
        });

        await h.Runtime(Model(AskAgenda(), AgentHarness.Reply("Hay espacio.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        var listed = Assert.Single(ToolResult().GetProperty("doctors").EnumerateArray());
        Assert.Equal(["doctorId", "doctor", "place", "slots"], listed.EnumerateObject().Select(p => p.Name));
        Assert.Contains(AgentHarness.Options(h.Interactive[^1]), option => option.Id.StartsWith("CITA ")); // the free hour is still offered
        Assert.Equal("agent", (await h.Fresh()).Status);
        Assert.DoesNotContain(await Trail(), a => a.Kind is "handoff_offer" or "handoff" or "error");
    }

    [Fact]
    public async Task HospitalIsAskedForTheDisciplinesOnceAnHourNotOnEveryTurn()
    {
        // The read costs Hospital one identity-provider call per staff account.
        var dentist = Guid.NewGuid(); object[] roll = [new { clinicianId = dentist, discipline = "dentistry" }];
        var hospital = h.Hospital(availability: Agenda((dentist, "Dra. Sintética Uno")), disciplines: roll);

        await h.Runtime(Model(AskAgenda(), AskAgenda(), AgentHarness.Reply("Hay espacio.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        await h.Say("patient", "¿Y tienen dentista?");
        await h.Runtime(Model(AskAgenda(), AgentHarness.Reply("Sí, odontología.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None); // another turn, another runtime, the same hospital

        Assert.Equal(1, h.DisciplineReads);
        Assert.Equal("odontología", ToolResult().GetProperty("doctors")[0].GetProperty("discipline").GetString()); // and the kept answer is still used
    }

    readonly List<string> asked = [];
    /// <summary>Hospital's double, keeping which paths were asked of it.</summary>
    AgentHarness.Fake Recording(AgentHarness.Fake hospital) { var inner = new HttpMessageInvoker(hospital); return new(request => { asked.Add(request.RequestUri!.AbsolutePath); return inner.SendAsync(request, CancellationToken.None); }); }

    [Fact]
    public async Task WhichDisciplinesTheHospitalHasIsAnsweredFromItsRollWithoutReadingTheAgenda()
    {
        var dentist = Guid.NewGuid();
        object[] roll = [new { clinicianId = dentist, discipline = "dentistry" }, new { clinicianId = Guid.NewGuid(), discipline = "dentistry" }, new { clinicianId = Guid.NewGuid(), discipline = "medicine" }, new { clinicianId = Guid.NewGuid(), discipline = (string?)null }, new { clinicianId = Guid.NewGuid(), discipline = "nursing" }];
        // The dentist has no free hour at all: the agenda only lists someone else. The hospital still has a dentist.
        var hospital = Recording(h.Hospital(availability: Agenda((Guid.NewGuid(), "Dr. Sintético Dos")), disciplines: roll));
        var reply = "Sí, en el hospital hay odontología.";

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_disciplines", new { }), AgentHarness.Reply(reply)), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        Assert.Equal(["medicina", "odontología"], ToolResult().GetProperty("disciplines").EnumerateArray().Select(d => d.GetString()!));
        Assert.Equal(["/v1/clinic/clinician-disciplines"], asked); // nothing else of Hospital, and not its agenda
        Assert.Equal([reply], h.Sent);
        Assert.Empty(h.Interactive); // an answer, not a list of hours to tap
    }

    [Theory]
    [InlineData(false)] // a Hospital that does not have the read yet
    [InlineData(true)]  // or one that knows nobody's discipline
    public async Task WithNoDisciplinesKnownTheGuideAnswers(bool unknown)
    {
        await Guide("Especialidades: medicina general y odontología.");
        object[] roll = [new { clinicianId = Guid.NewGuid(), discipline = (string?)null }];
        var reply = "Sí, tenemos odontología.";

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_disciplines", new { }), AgentHarness.Reply(reply)), h.Sender(), unknown ? h.Hospital(disciplines: roll) : h.Hospital()).Run(h.Job, CancellationToken.None);

        var result = ToolResult();
        Assert.False(result.GetProperty("found").GetBoolean()); Assert.Contains("guía", result.GetProperty("instruction").GetString()); Assert.False(result.TryGetProperty("error", out _));
        Assert.Equal([reply], h.Sent);
        Assert.DoesNotContain(await Trail(), a => a.Kind is "guard" or "handoff_offer" or "handoff");
    }

    [Fact]
    public async Task AskingForADisciplineTheHospitalDoesNotListIsNeverAnsweredWithSomeoneElsesHours()
    {
        // «¿Tienen nutricionista?», and only a physician is on the roll. Answered through the agenda this ended in «Hay espacio el lunes…» with
        // the physician's hours (an implied yes) and, with nothing free soon, in «¿Es una emergencia?». Through the roll the agenda is never read.
        (await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id)).EmergencyPhone = "2200 0000"; await h.Db.SaveChangesAsync();
        await h.Say("patient", "¿Tienen nutricionista?");
        var physician = Guid.NewGuid(); object[] roll = [new { clinicianId = physician, discipline = "medicine" }];
        var hospital = Recording(h.Hospital(availability: Agenda((physician, "Dr. Sintético Dos")), disciplines: roll));

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_disciplines", new { }), AgentHarness.ToolCall("handoff", new { reason = "Pregunta por nutricionista; no consta en la lista" }), AgentHarness.Reply("")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        var sent = Assert.Single(h.Sent);
        Assert.Contains(AgentGuard.PersonQuestion, sent);
        Assert.DoesNotContain("Hay espacio", sent); Assert.DoesNotContain(AgentGuard.EmergencyQuestion, sent); Assert.DoesNotContain("nutri", sent);
        Assert.Equal(["PERSONA", "MENU"], AgentHarness.Options(Assert.Single(h.Interactive)).Select(option => option.Id)); // the offer of a person, not hours to tap
        Assert.DoesNotContain("/v1/agenda/booking-options", asked);
        Assert.Equal("agent", (await h.Fresh()).Status);
    }

    [Fact]
    public async Task WhichDisciplinesThereAreCanBeAnsweredEvenBesideAForeignIdentifier()
    {
        await h.Say("patient", "Soy el paciente aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee, ¿tienen dentista?");
        object[] roll = [new { clinicianId = Guid.NewGuid(), discipline = "dentistry" }];

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_disciplines", new { }), AgentHarness.Reply("Sí, hay odontología.")), h.Sender(), h.Hospital(disciplines: roll)).Run(h.Job, CancellationToken.None);

        Assert.Equal("odontología", ToolResult().GetProperty("disciplines")[0].GetString());
    }

    [Fact]
    public async Task TheMenuNeverAsksHospitalForDisciplines()
    {
        var doctor = Guid.NewGuid(); object[] roll = [new { clinicianId = doctor, discipline = "medicine" }];
        await h.Say("patient", "AGENDAR");

        await h.Runtime(NoModel, h.Sender(), h.Hospital(availability: Agenda((doctor, "Dra. Sintética Uno")), disciplines: roll)).Run(h.Job, CancellationToken.None);

        Assert.Equal(0, h.DisciplineReads);
        Assert.Contains(AgentHarness.Options(h.Interactive[^1]), option => option.Id.StartsWith("CITA "));
    }
}
