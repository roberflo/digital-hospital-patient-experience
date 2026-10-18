using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>docs/reception-agent.md, criterios 68–74: the agent answers from Hospital's catalogue whether a service exists; a price or a
/// company discount only when the hospital turned prices on, and never a figure no source gave. The model is simulated.</summary>
public sealed class AgentCatalogTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    static readonly AgentHarness.Fake NoModel = new(_ => throw new InvalidOperationException("The model must not be consulted"));

    // Hospital's own wire shape, with everything the agent must never repeat: ids, codes, versions, tax ids, emails and phones.
    static readonly object[] Catalogue =
    [
        new { id = Guid.NewGuid(), code = "SRV-CONS", name = "Consulta general", kind = "consultation", price = 25.00m, currency = "USD", active = true, version = 3, discipline = "medicine" },
        new { id = Guid.NewGuid(), code = "SRV-LIMP", name = "Limpieza dental", kind = "procedure", price = 0m, currency = "USD", active = true, version = 1, discipline = "dentistry" }, // the dental starter catalogue ships without prices
    ];
    static object Company(string name, string taxId, bool active, bool agreement) => new { id = Guid.NewGuid(), name, taxId, email = "convenios@empresa.example.invalid", phone = "70000001", active, version = 7, agreement = new { name = "Convenio corporativo", discountPercent = 10m, active = agreement } };
    // The double answers every company whatever was asked. Hospital's own «q» looks in the name only; the agent's filter is tested as if it looked anywhere.
    static readonly object[] Companies = [Company("Empresa Sintética Uno", "NIT-SINT-A", true, true), Company("Empresa Sintética Dos", "NIT-SINT-B", true, false), Company("Empresa Sintética Cuatro", "NIT-SINT-D", false, true), Company("Comercial Tres", "EMPRESA-SINT-C", true, true)];

    readonly List<string> seen = [];
    /// <summary>Replays the given model responses and keeps what the model was sent, so a test reads the tool result the model saw.</summary>
    AgentHarness.Fake Model(params HttpResponseMessage[] turns)
    {
        var bodies = turns.Select(t => t.Content.ReadAsStringAsync().Result).ToArray(); var i = 0;
        return new(async request => { seen.Add(await request.Content!.ReadAsStringAsync()); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(bodies[Math.Min(i++, bodies.Length - 1)], Encoding.UTF8, "application/json") }; });
    }
    static readonly JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    /// <summary>The result of the last tool call, as the model received it. Written back without \u escapes: an assertion that «Sintética»
    /// is absent would otherwise pass against «Sintética».</summary>
    string ToolResult()
    {
        using var sent = JsonDocument.Parse(seen[^1]); var content = sent.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "tool").GetProperty("content").GetString()!;
        try { using var json = JsonDocument.Parse(content); return JsonSerializer.Serialize(json.RootElement, Plain); } catch (JsonException) { return content; } // a refusal of the framework itself is plain text
    }
    List<string?> Offered() { using var sent = JsonDocument.Parse(seen[0]); return sent.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToList(); }
    async Task Prices(string prices) { (await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id)).Attention = new Attention(Prices: prices).Write(); await h.Db.SaveChangesAsync(); }
    async Task<List<Recepcion.Activity>> Trail() => await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).ToListAsync();

    [Fact]
    public async Task WithPricesOffTheCatalogueSaysWhatThereIsAndNeverWhatItCosts()
    {
        var reply = "Sí, la consulta general consta entre nuestros servicios. El precio te lo confirma recepción.";

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_services", new { words = "consulta" }), AgentHarness.Reply(reply)), h.Sender(), h.Hospital(services: Catalogue)).Run(h.Job, CancellationToken.None);

        var result = ToolResult();
        Assert.Contains("Consulta general", result); Assert.Contains("recepción", result);
        Assert.DoesNotContain("listPrice", result); Assert.DoesNotContain("25", result); Assert.DoesNotContain("USD", result);
        Assert.DoesNotContain("SRV-", result); Assert.DoesNotContain("\"id\"", result); Assert.DoesNotContain("version", result);
        Assert.Equal([reply], h.Sent);
    }

    [Fact]
    public async Task WithPricesOnTheListPriceIsSaidExactlyAsHospitalHasIt()
    {
        await Prices("on");
        var reply = "La consulta general tiene un precio de lista de $25.00 USD.";

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_services", new { words = "consulta" }), AgentHarness.Reply(reply)), h.Sender(), h.Hospital(services: Catalogue)).Run(h.Job, CancellationToken.None);

        using var result = JsonDocument.Parse(ToolResult());
        Assert.Equal("25.00 USD", result.RootElement.GetProperty("services")[0].GetProperty("listPrice").GetString());
        Assert.DoesNotContain("SRV-", result.RootElement.GetRawText());
        Assert.Equal([reply], h.Sent);
        Assert.DoesNotContain(await Trail(), a => a.Kind == "guard");
    }

    [Fact]
    public async Task AServiceWithoutAPriceIsNeverQuotedAtZero()
    {
        await Prices("on");

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_services", new { }), AgentHarness.Reply("La limpieza dental consta; su precio te lo confirma recepción.")), h.Sender(), h.Hospital(services: Catalogue)).Run(h.Job, CancellationToken.None);

        using var result = JsonDocument.Parse(ToolResult());
        var dental = result.RootElement.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "Limpieza dental");
        Assert.False(dental.TryGetProperty("listPrice", out _));
        Assert.Equal("dentistry", dental.GetProperty("discipline").GetString());
    }

    [Fact]
    public async Task AServiceTheCatalogueDoesNotHaveDoesNotConst()
    {
        await h.Runtime(Model(AgentHarness.ToolCall("hospital_services", new { words = "resonancia" }), AgentHarness.Reply("Ese servicio no me consta. ¿Quieres que pase tu consulta a recepción?")), h.Sender(), h.Hospital()).Run(h.Job, CancellationToken.None);

        using var result = JsonDocument.Parse(ToolResult());
        Assert.False(result.RootElement.GetProperty("found").GetBoolean());
        Assert.Contains("no consta", result.RootElement.GetProperty("instruction").GetString());
    }

    [Theory]
    [InlineData("La consulta general cuesta $9.")]      // the «09:00» of the reminders is in the grounding: a figure there is not a price
    [InlineData("La consulta general cuesta $40.00.")]
    [InlineData("Con dos consultas serían 50 dólares.")] // arithmetic over a real price is still a figure nobody gave
    public async Task AnAmountNoSourceGaveIsWithheldAndAPersonIsOffered(string reply)
    {
        await Prices("on");

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_services", new { words = "consulta" }), AgentHarness.Reply(reply)), h.Sender(), h.Hospital(services: Catalogue)).Run(h.Job, CancellationToken.None);

        Assert.DoesNotContain(h.Sent, text => text == reply);
        Assert.Contains(AgentGuard.PersonQuestion, Assert.Single(h.Sent));
        Assert.Contains("precio", (await h.Db.Activities.SingleAsync(a => a.Kind == "guard")).Body);
        Assert.Contains(await Trail(), a => a.Kind == "handoff_offer");
        Assert.Equal("agent", (await h.Fresh()).Status);
    }

    [Fact]
    public async Task WithPricesOffThereIsNoAgreementToolAtAll()
    {
        await h.Runtime(Model(AgentHarness.ToolCall("hospital_agreement", new { company = "Empresa Sintética Uno" }), AgentHarness.Reply("Eso te lo confirma recepción.")), h.Sender(), h.Hospital(companies: Companies)).Run(h.Job, CancellationToken.None);

        Assert.Contains("hospital_services", Offered()); Assert.DoesNotContain("hospital_agreement", Offered());
        Assert.DoesNotContain("Sintética Uno", ToolResult()); Assert.DoesNotContain("10", ToolResult()); // called anyway, it answers nothing
    }

    [Fact]
    public async Task AnAgreementAnswersOnlyTheCompanyNamedAndItsPercentage()
    {
        await Prices("on"); await h.Say("patient", "trabajo en empresa sintetica uno, ¿tienen convenio?"); // as people type: no capitals, no accents
        var reply = "Sí consta un convenio con Empresa Sintética Uno: 10 % de descuento, que recepción aplica al pagar.";

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_agreement", new { company = "Empresa Sintética Uno" }), AgentHarness.Reply(reply)), h.Sender(), h.Hospital(companies: Companies)).Run(h.Job, CancellationToken.None);

        Assert.Contains("hospital_agreement", Offered());
        using var result = JsonDocument.Parse(ToolResult());
        var only = result.RootElement;
        Assert.Equal(["company", "agreement", "discount"], only.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Empresa Sintética Uno", only.GetProperty("company").GetString()); Assert.Equal("10 %", only.GetProperty("discount").GetString());
        var raw = result.RootElement.GetRawText();
        Assert.DoesNotContain("NIT-", raw); Assert.DoesNotContain("example.invalid", raw); Assert.DoesNotContain("70000001", raw); Assert.DoesNotContain("\"id\"", raw);
        Assert.Equal([reply], h.Sent);
    }

    [Theory]
    [InlineData("Em")]             // two letters would match half the companies
    [InlineData("   ")]
    [InlineData("Empresa Ausente")]
    public async Task AgreementsCannotBeListedOnlyAskedByName(string company)
    {
        await Prices("on"); await h.Say("patient", $"Trabajo en {company}, ¿qué convenio tengo?");

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_agreement", new { company }), AgentHarness.Reply("No me consta un convenio con ese nombre; recepción lo confirma.")), h.Sender(), h.Hospital(companies: Companies)).Run(h.Job, CancellationToken.None);

        Assert.DoesNotContain("Sintética", ToolResult()); Assert.DoesNotContain("Comercial", ToolResult()); Assert.DoesNotContain("10 %", ToolResult());
    }

    [Theory]
    [InlineData("Empresa Sintética")] // a word the patient did write, and two companies answer to it: neither is named
    [InlineData("S.A.")]
    public async Task ANameThatFitsTwoCompaniesAnswersNeither(string company)
    {
        await Prices("on"); await h.Say("patient", "Trabajo en una empresa sintética S.A., ¿qué descuento tengo?");
        object[] two = [Company("Empresa Sintética Uno S.A.", "NIT-SINT-A", true, true), Company("Empresa Sintética Cinco S.A.", "NIT-SINT-E", true, true)];

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_agreement", new { company }), AgentHarness.Reply("¿Cuál es el nombre completo de tu empresa?")), h.Sender(), h.Hospital(companies: two)).Run(h.Job, CancellationToken.None);

        using var result = JsonDocument.Parse(ToolResult());
        Assert.Equal(["found", "instruction"], result.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("Uno", ToolResult()); Assert.DoesNotContain("Cinco", ToolResult()); Assert.DoesNotContain("%", ToolResult()); Assert.DoesNotContain("10", ToolResult());
    }

    [Fact]
    public async Task ACompanyThePatientNeverNamedIsNotLookedUp()
    {
        // The model walking the client list: «Banco», «Grupo», a name it guessed. Only what the patient wrote is asked of Hospital.
        await Prices("on"); await h.Say("patient", "¿Con qué empresas tienen convenio?");
        var hospital = new AgentHarness.Fake(_ => throw new InvalidOperationException("Hospital must not be consulted"));

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_agreement", new { company = "Empresa Sintética Uno" }), AgentHarness.Reply("¿Cómo se llama tu empresa?")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        Assert.Equal(0, hospital.Calls);
        Assert.Contains("nombre", ToolResult()); Assert.DoesNotContain("%", ToolResult());
        Assert.Equal(["¿Cómo se llama tu empresa?"], h.Sent);
    }

    [Fact]
    public async Task TheCompanyCalledExactlyThatIsTheOneAskedFor()
    {
        await Prices("on"); await h.Say("patient", "Soy de Empresa Sintética, ¿tengo convenio?");
        object[] two = [Company("Empresa Sintética", "NIT-SINT-A", true, true), Company("Empresa Sintética Cinco", "NIT-SINT-E", true, true)];

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_agreement", new { company = "Empresa Sintética" }), AgentHarness.Reply("Sí consta: 10 % de descuento, que recepción aplica al pagar.")), h.Sender(), h.Hospital(companies: two)).Run(h.Job, CancellationToken.None);

        using var result = JsonDocument.Parse(ToolResult());
        Assert.Equal("Empresa Sintética", result.RootElement.GetProperty("company").GetString());
    }

    [Theory]
    [InlineData("Con ese convenio tienes 15 % de descuento.")]
    [InlineData("Con ese convenio tienes 25 % de descuento.")]   // 25.00 USD is the list price: a price does not ground a discount
    [InlineData("Con el convenio la consulta te queda en $22.50.")] // the discount applied: reception does that, not the agent
    public async Task ADiscountOrADiscountedAmountNoSourceGaveIsWithheld(string reply)
    {
        await Prices("on"); await h.Say("patient", "Trabajo en Empresa Sintética Uno, ¿cuánto me sale la consulta?");
        var model = Model(AgentHarness.ToolCall("hospital_services", new { words = "consulta" }), AgentHarness.ToolCall("hospital_agreement", new { company = "Empresa Sintética Uno" }), AgentHarness.Reply(reply));

        await h.Runtime(model, h.Sender(), h.Hospital(services: Catalogue, companies: Companies)).Run(h.Job, CancellationToken.None);

        Assert.DoesNotContain(h.Sent, text => text == reply);
        Assert.Contains("precio", (await h.Db.Activities.SingleAsync(a => a.Kind == "guard")).Body);
        Assert.Contains(await Trail(), a => a.Kind == "handoff_offer");
    }

    [Theory]
    [InlineData("results", "results", "*Motivo:* Entrega de resultados")]
    [InlineData("procedure", "procedure", "*Motivo:* Procedimiento")]
    [InlineData("paperwork", "paperwork", "*Motivo:* Trámite")]
    [InlineData(null, "follow-up", null)] // what the patient did not say is derived from Hospital (VisitKindTests)
    [InlineData("", "follow-up", null)]   // a model that fills the optional argument with nothing said nothing
    public async Task TheAppointmentIsBookedForWhatThePatientSaidItIsFor(string? kind, string booked, string? line)
    {
        await h.Link();
        var doctor = Guid.NewGuid(); var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero);
        var hospital = h.Hospital(availability: new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = doctor, clinicianName = "Dra. Sintética", placeName = "", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = start, durationMinutes = 30, takenBy = 0, offered = true } } } } } } },
            appointments: [new { appointmentId = Guid.NewGuid(), clinicianId = doctor, clinicianName = "Dra. Sintética", scheduledStart = DateTimeOffset.UtcNow.AddDays(-10), durationMinutes = 30, status = "booked" }]); // seen before: what is not said is a follow-up (MR-42)

        await h.Runtime(Model(AgentHarness.ToolCall("propose_action", new { action = "create", doctorId = doctor, startsAt = start, durationMinutes = 30, kind }), AgentHarness.Reply("Listo.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        var card = Assert.Single(h.Sent);
        if (line is null) Assert.DoesNotContain("Motivo", card); else Assert.Contains(line, card);
        Assert.Empty(h.HospitalWrites); // nothing before the patient confirms
        await h.Say("patient", "CONFIRMAR " + Assert.Single(await h.Pending("proposal")).Code);
        await h.Runtime(NoModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        Assert.Equal(["/v1/agenda"], h.HospitalWrites);
        using var body = JsonDocument.Parse(Assert.Single(h.HospitalBodies));
        Assert.Equal(booked, body.RootElement.GetProperty("visitKind").GetString());
    }

    /// <summary>The usual path: the agent shows the free hours and the patient taps one, which is proposed without the model. What the patient
    /// said the appointment is for, given when the agent looked up the hours, still reaches Hospital (found live: it was booked as a follow-up).</summary>
    [Theory]
    [InlineData("results", "results", "*Motivo:* Entrega de resultados")]
    [InlineData(null, "follow-up", null)]
    public async Task ATappedHourIsBookedForWhatThePatientSaidItIsFor(string? kind, string booked, string? line)
    {
        await h.Link();
        var doctor = Guid.NewGuid(); var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero);
        var hospital = h.Hospital(availability: new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = doctor, clinicianName = "Dra. Sintética", placeName = "", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = start, durationMinutes = 30, takenBy = 0, offered = true } } } } } } },
            appointments: [new { appointmentId = Guid.NewGuid(), clinicianId = doctor, clinicianName = "Dra. Sintética", scheduledStart = DateTimeOffset.UtcNow.AddDays(-10), durationMinutes = 30, status = "booked" }]); // seen before: what is not said is a follow-up (MR-42)

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_availability", new { date = start.ToString("yyyy-MM-dd"), kind }), AgentHarness.Reply("Estos son los horarios.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        await h.Say("patient", $"CITA {start.ToOffset(TimeSpan.FromHours(-6)):yyyy-MM-ddTHH:mm:sszzz} {doctor:D} 30 Dra. Sintética");
        await h.Runtime(NoModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        if (line is null) Assert.DoesNotContain("Motivo", h.Sent[^1]); else Assert.Contains(line, h.Sent[^1]);
        await h.Say("patient", "CONFIRMAR " + Assert.Single(await h.Pending("proposal")).Code);
        await h.Runtime(NoModel, h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        using var body = JsonDocument.Parse(Assert.Single(h.HospitalBodies));
        Assert.Equal(booked, body.RootElement.GetProperty("visitKind").GetString());
    }

    [Theory]
    [InlineData("same-day-urgent")] // urgency is triage: it goes through the emergency question and handoff, never through a kind the model picks
    [InlineData("first-visit-vip")]
    [InlineData("follow-up")]       // first visit or follow-up is the system's to derive: someone just registered is not a follow-up because the model said so
    [InlineData("first-visit")]
    public async Task AKindOutsideTheClosedSetProposesNothing(string kind)
    {
        await h.Link();
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero);

        await h.Runtime(Model(AgentHarness.ToolCall("propose_action", new { action = "create", doctorId = Guid.NewGuid(), startsAt = start, durationMinutes = 30, kind }), AgentHarness.Reply("No pude preparar la cita.")), h.Sender(), h.Hospital()).Run(h.Job, CancellationToken.None);

        Assert.Contains("error", ToolResult());
        Assert.Empty(await h.Pending("proposal"));
    }

    [Fact]
    public async Task MovingAnAppointmentDoesNotChangeWhatItIsFor()
    {
        await h.Link();
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero);

        await h.Runtime(Model(AgentHarness.ToolCall("propose_action", new { action = "reschedule", appointmentId = Guid.NewGuid(), doctorId = Guid.NewGuid(), startsAt = start, durationMinutes = 30, kind = "results" }), AgentHarness.Reply("Listo.")), h.Sender(), h.Hospital()).Run(h.Job, CancellationToken.None);

        Assert.DoesNotContain("Motivo", Assert.Single(h.Sent));
        Assert.DoesNotContain("kind", Assert.Single(await h.Pending("proposal")).Body);
    }

    [Fact]
    public async Task WithPricesOffAPriceTheGuideStatesIsStillSaid()
    {
        // As before the catalogue existed: the switch governs the catalogue, never what the hospital wrote in its own guide.
        (await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id)).Guide = "Consulta general: $25. Horario: lunes a viernes."; await h.Db.SaveChangesAsync();
        var reply = "La consulta general cuesta $25.";

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_services", new { words = "consulta general" }), AgentHarness.Reply(reply)), h.Sender(), h.Hospital(services: Catalogue)).Run(h.Job, CancellationToken.None);

        Assert.Contains("guía", ToolResult()); Assert.DoesNotContain("listPrice", ToolResult());
        Assert.Equal([reply], h.Sent);
        Assert.DoesNotContain(await Trail(), a => a.Kind is "guard" or "handoff_offer");
    }

    [Theory]
    [InlineData(false)] // Hospital answers 503
    [InlineData(true)]  // Hospital does not answer at all
    public async Task WithTheCatalogueDownTheGuideStillAnswers(bool unreachable)
    {
        (await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id)).Guide = "Servicios: consulta general y laboratorio. Consulta general: $25."; await h.Db.SaveChangesAsync();
        var hospital = new AgentHarness.Fake(_ => unreachable ? throw new HttpRequestException("synthetic outage") : Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }));
        var reply = "Sí, ofrecemos consulta general. Cuesta $25.";

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_services", new { words = "consulta" }), AgentHarness.Reply(reply)), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        using var result = JsonDocument.Parse(ToolResult());
        Assert.False(result.RootElement.GetProperty("available").GetBoolean());
        Assert.Contains("guía", result.RootElement.GetProperty("instruction").GetString()); Assert.False(result.RootElement.TryGetProperty("error", out _));
        Assert.Equal([reply], h.Sent);
        Assert.DoesNotContain(await Trail(), a => a.Kind is "guard" or "handoff_offer" or "handoff");
        Assert.Equal("agent", (await h.Fresh()).Status);
    }

    [Fact]
    public async Task AnEmptyCatalogueDefersToTheGuide()
    {
        await h.Runtime(Model(AgentHarness.ToolCall("hospital_services", new { words = "laboratorio" }), AgentHarness.Reply("Ese servicio no me consta.")), h.Sender(), h.Hospital()).Run(h.Job, CancellationToken.None);

        Assert.Contains("Si la guía lo dice, responde con la guía", ToolResult());
    }

    [Fact]
    public async Task APriceInACurrencyTheGuardCannotCheckIsLeftToReception()
    {
        await Prices("on");
        object[] quetzales = [new { id = Guid.NewGuid(), code = "SRV-GTQ", name = "Consulta externa", kind = "consultation", price = 200.00m, currency = "GTQ", active = true, version = 1, discipline = "medicine" }];

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_services", new { }), AgentHarness.Reply("La consulta externa consta; su precio te lo confirma recepción.")), h.Sender(), h.Hospital(services: quetzales)).Run(h.Job, CancellationToken.None);

        Assert.Contains("Consulta externa", ToolResult());
        Assert.DoesNotContain("listPrice", ToolResult()); Assert.DoesNotContain("200", ToolResult()); Assert.DoesNotContain("GTQ", ToolResult());
    }

    [Fact]
    public async Task APhraseThatFindsNothingIsAskedAgainWithItsLongestWord()
    {
        // Hospital looks for the whole phrase inside the name: «limpieza de dientes» is not in «Limpieza dental».
        var asked = new List<string>();
        var hospital = new AgentHarness.Fake(request =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["q"] ?? ""; asked.Add(q); object[] items = q == "limpieza" ? Catalogue : [];
            return Task.FromResult(AgentHarness.Json(new { items, total = items.Length, page = 1, pageSize = 100 }));
        });

        await h.Runtime(Model(AgentHarness.ToolCall("hospital_services", new { words = "limpieza de dientes" }), AgentHarness.Reply("Sí, la limpieza dental consta entre nuestros servicios.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        Assert.Equal(["limpieza de dientes", "limpieza"], asked);
        Assert.Contains("Limpieza dental", ToolResult());
    }

    [Theory]
    [InlineData("Cuesta 40$.", "{\"listPrice\":\"40.00 USD\"}", null)]                     // the sign behind
    [InlineData("Cuesta 40 $ por sesión.", "{\"listPrice\":\"40.00 USD\"}", null)]
    [InlineData("Cuesta 40$.", "{\"listPrice\":\"25.00 USD\"}", "precio")]
    [InlineData("Cuesta 1 dólar.", "{\"listPrice\":\"1.00 USD\"}", null)]
    [InlineData("Cuesta 1 dólar.", "{\"listPrice\":\"25.00 USD\"}", "precio")]
    [InlineData("It costs 40 dollars.", "{\"listPrice\":\"40.00 USD\"}", null)]
    [InlineData("It costs 45 dollars.", "{\"listPrice\":\"40.00 USD\"}", "precio")]
    [InlineData("Tienes 10 porciento de descuento.", "{\"discount\":\"10 %\"}", null)]
    [InlineData("Tienes 15 porciento de descuento.", "{\"discount\":\"10 %\"}", "precio")]
    [InlineData("Tienes 10 pct de descuento.", "{\"discount\":\"10 %\"}", null)]
    [InlineData("Tienes 15 pct de descuento.", "{\"discount\":\"10 %\"}", "precio")]
    [InlineData("Cuesta $25.000.", "{\"listPrice\":\"25.00 USD\"}", "precio")]            // three digits after the mark are thousands: twenty-five thousand
    [InlineData("Cuesta $1.200,50.", "{\"listPrice\":\"1200.50 USD\"}", null)]            // thousands and decimals, the Spanish way
    [InlineData("Cuesta $1,200.50.", "Tarifa: 1.200,50 dólares", null)]                  // and the other way round
    [InlineData("Cuesta $1.200.", "{\"listPrice\":\"1200.00 USD\"}", null)]
    [InlineData("Cuesta $25 mil.", "{\"listPrice\":\"25.00 USD\"}", "precio")]            // scaled in words: no source backs it
    [InlineData("Cuesta 25 mil dólares.", "Tarifa: 25 mil dólares", "precio")]
    [InlineData("Cuesta 2 millones de dólares.", "{\"listPrice\":\"2.00 USD\"}", "precio")]
    [InlineData("Hay dos opciones, la 1, $25 la consulta.", "{\"listPrice\":\"25.00 USD\"}", null)] // the 1 before the sign is not «1 $»
    [InlineData("La consulta cuesta 40.", "", null)]                                      // the declared ceiling: a figure with no mark is not seen
    public void MoneyIsRecognisedHoweverItIsWritten(string reply, string grounding, string? violation) => Assert.Equal(violation, AgentGuard.Outbound(reply, grounding, false));

    [Theory]
    [InlineData("La consulta cuesta $25.", "{\"listPrice\":\"25.00 USD\"}", null)]
    [InlineData("La consulta cuesta 25 dólares.", "Consulta general: USD 25", null)]
    [InlineData("Son $1,200 en total.", "{\"listPrice\":\"1200.00 USD\"}", null)]          // the same amount, written three ways
    [InlineData("Cuesta $12,50.", "{\"listPrice\":\"12.50 USD\"}", null)]                 // a lone comma is a decimal comma
    [InlineData("Tienes 10% de descuento.", "{\"discount\":\"10 %\"}", null)]
    [InlineData("Tienes 10 por ciento de descuento.", "{\"discount\":\"10 %\"}", null)]
    [InlineData("Cuesta $30.", "{\"durationMinutes\":30,\"time\":\"09:30\"}", "precio")]  // thirty minutes are not thirty dollars
    [InlineData("Tienes 15 % de descuento.", "{\"discount\":\"10 %\"}", "precio")]
    [InlineData("Tienes 25 % de descuento.", "{\"listPrice\":\"25.00 USD\"}", "precio")]  // a price is not a percentage
    [InlineData("Cuesta $10.", "{\"discount\":\"10 %\"}", "precio")]                      // nor a percentage a price
    [InlineData("Cuesta $1234567890123456789012345678901234567890.", "{\"listPrice\":\"25.00 USD\"}", "precio")] // too long to be a number: no match, no exception
    [InlineData("Cuesta $25.", "Saldo: 1234567890123456789012345678901234567890 USD", "precio")]
    [InlineData("Abrimos de lunes a viernes.", "", null)]
    public void MoneyInAReplyMustBeMoneyASourceGave(string reply, string grounding, string? violation) => Assert.Equal(violation, AgentGuard.Outbound(reply, grounding, false));

    [Fact]
    public void ThePatientsOwnFigureDoesNotGroundAPrice() => // «sí, cuesta $20» echoed back would be the hospital confirming it
        Assert.Equal("precio", AgentGuard.Outbound("Sí, la consulta cuesta $20.", "", false, "", "¿La consulta cuesta $20, verdad?"));
}
