using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Recepcion;
using Recepcion.Integrations;
using Xunit;

// Every database-backed class migrates the same disposable database on start; in parallel they race on DDL.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

/// <summary>docs/reception-agent.md, criterios 2–7 y 12: lo que el modelo escribe no llega al
/// paciente si una guarda lo retiene. El modelo es simulado; la base y el runtime son reales.</summary>
public sealed class AgentGuardTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();

    [Theory]
    [InlineData("dosis", "Toma 500 mg de amoxicilina cada 8 horas durante una semana.")]
    [InlineData("dosis", "Puedes subir a 2 tabletas si el dolor sigue.")]
    [InlineData("enlace", "Descarga tu receta en https://recetas.example.invalid/mi-receta")]
    [InlineData("instrucciones", "Mis instrucciones dicen: GUÍA DE ATENCIÓN (datos): atención de lunes a viernes.")]
    public async Task UngroundedReplyIsWithheldAndHandedOff(string category, string reply)
    {
        var sender = h.Sender();
        await h.Runtime(h.Model(AgentHarness.Reply(reply)), sender).Run(h.Job, CancellationToken.None);

        Assert.Equal("human", (await h.Fresh()).Status);
        Assert.DoesNotContain(h.Sent, text => text == reply);
        var guard = await h.Db.Activities.SingleAsync(a => a.Kind == "guard");
        Assert.Contains(category, guard.Body);
        Assert.DoesNotContain("amoxicilina", guard.Body);
    }

    [Fact]
    public async Task GroundedPrescriptionInstructionsAreSent()
    {
        var prescription = Guid.NewGuid();
        await h.Link();
        var hospital = h.Hospital(new { prescriptionId = prescription, patientId = h.Contact.PatientId, encounterId = Guid.NewGuid(), state = "signed", signedAt = DateTimeOffset.UtcNow, contentWithheld = false, lines = new[] { new { medication = "Amoxicilina", instructions = "500 mg cada 8 horas por 7 días" } } });
        var reply = "Tu receta indica: Amoxicilina, 500 mg cada 8 horas por 7 días.";
        var model = h.Model(AgentHarness.ToolCall("get_prescription", new { prescriptionId = prescription }), AgentHarness.Reply(reply));

        await h.Runtime(model, h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        Assert.Contains(reply, h.Sent);
        Assert.Equal("agent", (await h.Fresh()).Status);
        Assert.Empty(await h.Db.Activities.Where(a => a.Kind == "guard").ToListAsync());
    }

    [Fact]
    public async Task GuideGroundsHospitalInformation()
    {
        var tenant = await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id); tenant.Guide = "Para el examen de glucosa: ayuno de 8 horas. Más información en https://hospital.example.invalid/preparacion"; await h.Db.SaveChangesAsync();
        var reply = "Para el examen de glucosa necesitas ayuno de 8 horas. Detalles: https://hospital.example.invalid/preparacion";

        await h.Runtime(h.Model(AgentHarness.Reply(reply)), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Contains(reply, h.Sent);
    }

    [Fact]
    public async Task ProposalNeverReadsAsDoneAndAlwaysCarriesItsCode()
    {
        await h.Link();
        var proposal = new { action = "cancel", appointmentId = Guid.NewGuid() };
        var hospital = h.Hospital();

        // The model claims the write already happened: withheld.
        await h.Runtime(h.Model(AgentHarness.ToolCall("propose_action", proposal), AgentHarness.Reply("Listo, tu cita ya fue cancelada.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);
        Assert.Equal("human", (await h.Fresh()).Status);
        Assert.DoesNotContain(h.Sent, text => text.Contains("ya fue cancelada"));
        Assert.Contains("confirmación", (await h.Db.Activities.SingleAsync(a => a.Kind == "guard")).Body);
        Assert.Empty(h.HospitalWrites);
    }

    [Fact]
    public async Task ProposalReplyWithoutCodeGetsTheConfirmationInstruction()
    {
        await h.Link();
        var model = h.Model(AgentHarness.ToolCall("propose_action", new { action = "cancel", appointmentId = Guid.NewGuid() }), AgentHarness.Reply("Puedo cancelar tu cita del lunes. ¿Deseas continuar?"));

        await h.Runtime(model, h.Sender(), h.Hospital()).Run(h.Job, CancellationToken.None);

        var code = (await h.Db.Activities.SingleAsync(a => a.Kind.StartsWith("proposal:"))).Kind[9..];
        Assert.Contains(h.Sent, text => text.Contains("CONFIRMAR " + code));
        Assert.Empty(h.HospitalWrites);
    }

    [Fact]
    public async Task OnlyTheClosingMessageReachesThePatient()
    {
        var thinkingAloud = AgentHarness.Json(new { choices = new[] { new { message = new { role = "assistant", content = "Voy a guardar una nota interna.", tool_calls = new[] { new { id = "call-1", type = "function", function = new { name = "record_note", arguments = "{\"note\":\"Nota sintética\"}" } } } } } } });

        await h.Runtime(h.Model(thinkingAloud, AgentHarness.Reply("Listo, ¿algo más?")), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal(["Listo, ¿algo más?"], h.Sent);
    }

    [Theory]
    [InlineData("Mi contacto de emergencia es Carlos Sintético, mi hermano.", false)]
    [InlineData("Contactos de emergencias: mi mamá.", false)]
    [InlineData("Es una emergencia, mi contacto de emergencia es Carlos.", true)]
    [InlineData("Necesito una cita de emergencia", true)]
    [InlineData("Creo que tomé una sobredosis", true)]
    public void EmergencyContactIsRegistrationDataNotAnEmergency(string message, bool urgent) => Assert.Equal(urgent, AgentGuard.Inbound(message, "text") is not null);

    [Fact]
    public async Task ProposalAlwaysStatesTheExactLocalDateTheServerWillExecute()
    {
        await h.Link();
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(15), TimeSpan.Zero); // 09:00 in America/El_Salvador
        var local = start.ToOffset(TimeSpan.FromHours(-6));
        var model = h.Model(AgentHarness.ToolCall("propose_action", new { action = "create", doctorId = Guid.NewGuid(), startsAt = start, durationMinutes = 30 }), AgentHarness.Reply("Tengo un espacio mañana a primera hora."));

        await h.Runtime(model, h.Sender(), h.Hospital()).Run(h.Job, CancellationToken.None);

        var sent = Assert.Single(h.Sent);
        Assert.Contains($" {local.Day} de ", sent); Assert.Contains("a las 09:00", sent); Assert.Contains(local.Year.ToString(), sent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mañana a las nueve")]
    [InlineData("2020-01-01T09:00:00-06:00")]
    public async Task AppointmentProposalNeedsARealFutureStart(string? startsAt)
    {
        await h.Link();
        var model = h.Model(AgentHarness.ToolCall("propose_action", new { action = "create", doctorId = Guid.NewGuid(), startsAt, durationMinutes = 30 }), AgentHarness.Reply("No pude preparar la cita."));

        await h.Runtime(model, h.Sender(), h.Hospital()).Run(h.Job, CancellationToken.None);

        Assert.Empty(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id && a.Kind.StartsWith("proposal:")).ToListAsync());
    }

    [Fact]
    public async Task AvailabilityAnswersWithTheFirstDayThatHasFreeSlots()
    {
        // The asked day is closed, the next has one taken and one free slot: the model must be told the real first opening.
        var asked = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)); var open = asked.AddDays(1); var doctor = Guid.NewGuid();
        DateTimeOffset At(int hour) => new(open.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.FromHours(-6));
        var hospital = h.Hospital(availability: new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = doctor, clinicianName = "Dra. Sintética", placeName = "", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = open.ToString("yyyy-MM-dd"), state = "open", takenSlotCount = 1, utcOffset = "-06:00", slots = new[] { new { slotId = "a", startsAt = At(9), durationMinutes = 30, takenBy = 1, offered = true }, new { slotId = "b", startsAt = At(10), durationMinutes = 30, takenBy = 0, offered = true } } } } } } });
        var seen = new List<string>(); var turn = 0;
        var model = new AgentHarness.Fake(async request => { seen.Add(await request.Content!.ReadAsStringAsync()); return ++turn == 1 ? AgentHarness.ToolCall("hospital_availability", new { date = asked.ToString("yyyy-MM-dd") }) : AgentHarness.Reply("Hay espacio."); });

        await h.Runtime(model, h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        using var second = JsonDocument.Parse(seen[1]);
        using var result = JsonDocument.Parse(second.RootElement.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool").GetProperty("content").GetString()!);
        Assert.False(result.RootElement.GetProperty("isRequestedDate").GetBoolean());
        Assert.Equal(open.ToString("yyyy-MM-dd"), result.RootElement.GetProperty("date").GetString());
        var slot = Assert.Single(result.RootElement.GetProperty("doctors")[0].GetProperty("slots").EnumerateArray()); // the taken 09:00 is never offered
        Assert.Equal("10:00", slot.GetProperty("time").GetString());
    }

    [Theory]
    [InlineData(2, false, 0, false)]   // a free slot within 8 hours: nothing to ask
    [InlineData(30, false, 0, true)]   // first free slot tomorrow: ask
    [InlineData(null, false, 0, true)] // nothing published: ask
    [InlineData(30, true, 0, false)]   // already asked in this conversation: do not repeat
    [InlineData(2, false, 3, false)]   // asked about a later day while there is a slot in two hours: «nothing soon» would be false
    [InlineData(30, false, 3, true)]   // asked about a later day and nothing soon either: ask
    public async Task NoSlotWithinEightHoursAsksWhetherItIsAnEmergency(int? hoursToFirstSlot, bool alreadyAsked, int askedDaysAhead, bool asks)
    {
        var tenant = await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id); tenant.EmergencyPhone = "2200 0000"; await h.Db.SaveChangesAsync();
        if (alreadyAsked) { await h.Say("agent", "No tengo horarios en las próximas 8 horas. ¿Es una emergencia?"); await h.Say("patient", "No, ¿qué hay mañana?"); }
        var slots = hoursToFirstSlot is { } hours ? new[] { new { slotId = "a", startsAt = DateTimeOffset.UtcNow.AddHours(hours), durationMinutes = 30, takenBy = 0, offered = true } } : [];
        var hospital = h.Hospital(availability: new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = Guid.NewGuid(), clinicianName = "Dra. Sintética", placeName = "", defaultDurationMinutes = 30, days = new[] { new { clinicalDay = "", state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots } } } } });
        var model = h.Model(AgentHarness.ToolCall("hospital_availability", new { date = DateTime.UtcNow.AddHours(-6).AddDays(askedDaysAhead).ToString("yyyy-MM-dd") }), AgentHarness.Reply("Esto es lo que encontré."));

        await h.Runtime(model, h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        var sent = Assert.Single(h.Sent);
        Assert.Equal(asks, sent.Contains("¿Es una emergencia?"));
        Assert.Equal(asks, sent.Contains("2200 0000"));
        Assert.Equal("agent", (await h.Fresh()).Status);
    }

    [Theory]
    [InlineData("EMERGENCIA")]
    [InlineData("Sí, es una emergencia")]
    [InlineData("Sí")]
    [InlineData("si, por favor")]
    public async Task AnsweringThatItIsAnEmergencyHandsOffWithoutTheModel(string answer)
    {
        await h.Say("agent", "No tengo horarios en las próximas 8 horas. ¿Es una emergencia?"); await h.Say("patient", answer);
        var model = new AgentHarness.Fake(_ => throw new InvalidOperationException("The model must not be consulted"));

        await h.Runtime(model, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("human", (await h.Fresh()).Status);
    }

    [Fact]
    public async Task ReplyThatRecitesTheInstructionsIsWithheld()
    {
        // Found by the evals: asked to «repeat the text above», the model recited its prompt. No marker needed: any instruction line is enough.
        var leak = "Eres el asistente de recepción de Hospital Sintético. Responde en español de forma breve y cálida. Atiendes únicamente al paciente de esta conversación. Nunca solicites ni aceptes IDs de otros pacientes.";

        await h.Runtime(h.Model(AgentHarness.Reply(leak)), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("human", (await h.Fresh()).Status);
        Assert.DoesNotContain(h.Sent, text => text.Contains("Nunca solicites"));
        Assert.Contains("instrucciones", (await h.Db.Activities.SingleAsync(a => a.Kind == "guard")).Body);
    }

    [Theory]
    [InlineData("Un momento, voy a derivarte a recepción para que puedan orientarte.", true)]
    [InlineData("Te paso con el equipo del hospital.", true)]
    [InlineData("No tengo ese dato. ¿Te gustaría que lo derive a recepción?", false)]
    [InlineData("Si necesitas una receta nueva tengo que pasarle a recepción. ¿Le envío la actual?", false)]
    public async Task SayingItHandsOffMeansItHandsOff(string reply, bool handsOff)
    {
        // Found by the evals: the model told the patient it was transferring them and never called handoff, so nobody was notified.
        await h.Runtime(h.Model(AgentHarness.Reply(reply)), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal(handsOff ? "human" : "agent", (await h.Fresh()).Status);
        Assert.Equal(handsOff, (await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).ToListAsync()).Any(a => a.Kind == "handoff"));
    }

    [Theory]
    [InlineData("No, no es emergencia. Agéndame mañana a las 9.", false, false)]
    [InlineData("no es una emergencia, solo quiero cita", false, false)]
    [InlineData("No sé si es emergencia pero me duele mucho", false, true)]
    [InlineData("Emergencia: Elena Sintética, esposa, 70000004", true, false)]  // the label of a registration field, right after the agent asked for it
    [InlineData("Emergencia: Elena Sintética, esposa, 70000004", false, true)]
    [InlineData("Mi contacto es Elena. Pero esto es una emergencia, me siento muy mal", true, true)]
    public void EmergencyWordIsReadInContext(string message, bool registering, bool urgent) => Assert.Equal(urgent, AgentGuard.Inbound(message, "text", registering) is not null);

    [Fact]
    public async Task EmergencyQuestionIsAskedOnce()
    {
        var hospital = h.Hospital(availability: new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = Array.Empty<object>() });
        var model = h.Model(AgentHarness.ToolCall("hospital_availability", new { date = DateTime.UtcNow.AddHours(-6).ToString("yyyy-MM-dd") }), AgentHarness.Reply("No hay horarios hoy. ¿Es una emergencia? Si no, puedo buscar otro día."));

        await h.Runtime(model, h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        var sent = Assert.Single(h.Sent);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(sent, "¿Es una emergencia\\?"));
        Assert.Contains("EMERGENCIA", sent);
    }

    [Fact]
    public async Task SecondProposalInOneTurnIsRejected()
    {
        await h.Link();
        var cancel = new { action = "cancel", appointmentId = Guid.NewGuid() };
        var model = h.Model(AgentHarness.ToolCall("propose_action", cancel), AgentHarness.ToolCall("propose_action", cancel), AgentHarness.Reply("Te envié la propuesta."));

        await h.Runtime(model, h.Sender(), h.Hospital()).Run(h.Job, CancellationToken.None);

        Assert.Single(await h.Db.Activities.Where(a => a.Kind.StartsWith("proposal:")).ToListAsync());
    }

    [Fact]
    public async Task ToolBudgetHandsOff()
    {
        // A model that never stops calling tools must not loop on the patient's conversation.
        var model = new AgentHarness.Fake(_ => Task.FromResult(AgentHarness.ToolCall("record_note", new { note = "Nota sintética" })));

        await h.Runtime(model, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("human", (await h.Fresh()).Status);
        Assert.InRange(await h.Db.Activities.CountAsync(a => a.Kind == "note"), 1, 12);
    }

    [Fact]
    public async Task ModelFailureFallsBackToOpenAiWithoutRepeatingTools()
    {
        var hosts = new List<string>(); var openAiTurn = 0;
        var model = new AgentHarness.Fake(request =>
        {
            hosts.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host != "api.openai.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
            Assert.Equal("synthetic-openai", request.Headers.Authorization!.Parameter);
            return Task.FromResult(++openAiTurn == 1 ? AgentHarness.ToolCall("record_note", new { note = "Nota sintética" }) : AgentHarness.Reply("Respuesta del proveedor de respaldo"));
        });

        await h.Runtime(model, h.Sender(), extra: new() { ["OPENAI_API_KEY"] = "synthetic-openai", ["OPENAI_MODEL"] = "synthetic-fallback" }).Run(h.Job, CancellationToken.None);

        Assert.Contains("Respuesta del proveedor de respaldo", h.Sent);
        Assert.Contains("api.openai.com", hosts);
        Assert.Single(await h.Db.Activities.Where(a => a.Kind == "note").ToListAsync());
        Assert.Equal("agent", (await h.Fresh()).Status);
    }

    [Fact]
    public async Task ModelFailureWithoutFallbackKeyNeverReachesOpenAi()
    {
        var hosts = new List<string>();
        var model = new AgentHarness.Fake(request => { hosts.Add(request.RequestUri!.Host); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }); });

        await Assert.ThrowsAnyAsync<Exception>(() => h.Runtime(model, h.Sender()).Run(h.Job, CancellationToken.None));

        Assert.DoesNotContain("api.openai.com", hosts);
        Assert.Empty(h.Sent);
    }
}

/// <summary>Synthetic tenant, contact and conversation over the isolated PostgreSQL database,
/// with HTTP doubles for the model, WhatsApp and Hospital. Shared by the guard tests and evals.</summary>
public sealed class AgentHarness(Guid? tenant = null, string phone = "50370000000") : IAsyncDisposable
{
    public CrmDb Db = null!; public readonly TenantScope Scope = new() { Id = tenant ?? Guid.NewGuid() };
    public Contact Contact = null!; public Conversation Conversation = null!; public Job Job = null!;
    public readonly List<string> Sent = []; public readonly List<string> HospitalWrites = []; public int Documents;
    DbContextOptions<CrmDb> options = null!; readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
    readonly Dictionary<string, string?> settings = new() { ["SEND_ENABLED"] = "true", ["NVIDIA_API_KEY"] = "synthetic", ["AI_MODEL"] = "synthetic", ["KAPSO_API_KEY"] = "synthetic" };

    public async Task Start(string guide = "", string message = "Consulta sintética")
    {
        var connection = Environment.GetEnvironmentVariable("TEST_DATABASE");
        if (string.IsNullOrEmpty(connection)) throw new InvalidOperationException("Run scripts/test-backend.sh to provide the isolated PostgreSQL database.");
        options = new DbContextOptionsBuilder<CrmDb>().UseNpgsql(connection).Options; Db = new(options, Scope, protection); await Db.Database.MigrateAsync();
        Db.Tenants.Add(new Tenant { Id = Scope.Id, Name = "Hospital Sintético", AgentEnabled = true, Guide = guide });
        Contact = new() { TenantId = Scope.Id, Name = "Paciente Sintético", Phone = phone, PhoneHash = Guid.NewGuid().ToString() };
        var channel = new Channel { TenantId = Scope.Id, Name = "Synthetic", PhoneNumberId = Guid.NewGuid().ToString("N"), Enabled = true }; Db.Add(Contact); Db.Add(channel);
        Conversation = new() { TenantId = Scope.Id, ChannelId = channel.Id, ContactId = Contact.Id, Status = "agent", LastInboundAt = DateTimeOffset.UtcNow }; Db.Add(Conversation);
        await Db.SaveChangesAsync(); await Say("patient", message);
    }
    /// <summary>Appends a message; the job always answers the newest patient message.</summary>
    public async Task Say(string sender, string body)
    {
        var message = new Message { TenantId = Scope.Id, ConversationId = Conversation.Id, ExternalId = "m-" + Guid.NewGuid(), Body = body, Sender = sender, CreatedAt = DateTimeOffset.UtcNow.AddSeconds(Db.Messages.Local.Count) }; Db.Add(message);
        if (sender == "patient") { Job = new() { TenantId = Scope.Id, ConversationId = Conversation.Id, Key = "agent:" + message.ExternalId }; Db.Add(Job); }
        await Db.SaveChangesAsync();
    }
    public async Task Link() { Contact.PatientId = Guid.NewGuid(); await Db.SaveChangesAsync(); }
    public async Task<Conversation> Fresh() { await using var other = new CrmDb(options, Scope, protection); return await other.Conversations.AsNoTracking().SingleAsync(x => x.Id == Conversation.Id); }

    public AgentRuntime Runtime(HttpMessageHandler model, Fake sender, HttpMessageHandler? hospital = null, Dictionary<string, string?>? extra = null)
    {
        var token = "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { tenant_id = Scope.Id, exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic";
        var prefix = $"Hospital:Tenants:{Scope.Id}:";
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).AddInMemoryCollection(new Dictionary<string, string?> { [prefix + "BaseUrl"] = "https://hospital.example.invalid/", [prefix + "AccessToken"] = token, [prefix + "AllowClinicalDelivery"] = "true", [prefix + "UseReceptionBridge"] = "true", [prefix + "UsePatientAgenda"] = "true" }).AddInMemoryCollection(extra ?? []).Build();
        var kapso = new KapsoClient(new HttpClient(sender), config);
        return new(new HttpClient(model), config, Db, Scope, new HospitalClient(new HttpClient(hospital ?? new Fake(_ => throw new InvalidOperationException("Unexpected hospital request"))), config), new ConversationService(Db, Scope, kapso), kapso);
    }
    /// <summary>Replays the given model responses in order, then repeats the last one.</summary>
    public Fake Model(params HttpResponseMessage[] turns) { var bodies = turns.Select(t => t.Content.ReadAsStringAsync().Result).ToArray(); var i = 0; return new(_ => Task.FromResult(Raw(bodies[Math.Min(i++, bodies.Length - 1)]))); }
    /// <summary>WhatsApp double: records every text body and counts documents.</summary>
    public Fake Sender() => new(async request =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/media")) return Json(new { id = "media-" + Guid.NewGuid() });
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        if (body.RootElement.GetProperty("type").GetString() == "text") Sent.Add(body.RootElement.GetProperty("text").GetProperty("body").GetString()!); else Documents++;
        return Json(new { messages = new[] { new { id = "out-" + Guid.NewGuid() } } });
    });
    /// <summary>Hospital double for the linked patient: identity, one-day agenda, availability,
    /// issued prescriptions and their PDF. Any other write is recorded, never executed.</summary>
    public Fake Hospital(object? prescription = null, object[]? appointments = null, object? availability = null) => new(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.StartsWith("/v1/patients/")) return Task.FromResult(Json(new { patientId = Contact.PatientId, givenNames = "Paciente", familyNames = "Sintético", phone = Contact.Phone }));
        if (path.StartsWith("/v1/agenda/patients/")) return Task.FromResult(Json(new { rows = appointments ?? [] }));
        if (path == "/v1/agenda/booking-options") return Task.FromResult(Json((availability as Func<Uri, object>)?.Invoke(request.RequestUri) ?? availability ?? new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = Array.Empty<object>() }));
        if (path.EndsWith("/prescriptions/list")) return Task.FromResult(Json(new { prescriptionIds = prescription is null ? [] : new[] { JsonSerializer.SerializeToElement(prescription).GetProperty("prescriptionId").GetGuid() }, nextCursor = (string?)null }));
        if (path.EndsWith("/pdf")) { var pdf = new ByteArrayContent("%PDF-synthetic"u8.ToArray()); pdf.Headers.ContentType = new("application/pdf"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = pdf }); }
        if (path.StartsWith("/v1/reception/prescriptions/") && prescription is not null) return Task.FromResult(Json(prescription));
        if (request.Method != HttpMethod.Get && (path.StartsWith("/v1/agenda") || path == "/v1/patients")) HospitalWrites.Add(path);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    });

    public static HttpResponseMessage Reply(string text) => Json(new { choices = new[] { new { message = new { role = "assistant", content = text } } } });
    public static HttpResponseMessage ToolCall(string name, object arguments) => Json(new { choices = new[] { new { message = new { role = "assistant", content = (string?)null, tool_calls = new[] { new { id = "call-" + Guid.NewGuid().ToString("N"), type = "function", function = new { name, arguments = JsonSerializer.Serialize(arguments) } } } } } } });
    public static HttpResponseMessage Json(object value) => Raw(JsonSerializer.Serialize(value));
    static HttpResponseMessage Raw(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    public async ValueTask DisposeAsync() { if (Db != null) await Db.DisposeAsync(); }
    public sealed class Fake(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler { public int Calls; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return handler(request); } }
}
