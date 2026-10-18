using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Recepcion;
using Recepcion.Integrations;
using Xunit;
using Xunit.Abstractions;

/// <summary>One real conversation, end to end (scripts/live-agent.sh): a new client registers,
/// books the first free slot, asks a frequent question and reports an emergency. The agent, the
/// model and the LOCAL Hospital API are real; only WhatsApp is a double. Every run leaves one
/// synthetic patient and one appointment in the local Hospital.</summary>
public sealed class AgentLiveJourney(ITestOutputHelper output)
{
    [Fact, Trait("Category", "Live")]
    public async Task NewClientRegistersBooksAndIsAttended()
    {
        var env = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var tenant = Guid.Parse(env["DEV_HOSPITAL_TENANT_ID"] ?? throw new InvalidOperationException("DEV_HOSPITAL_TENANT_ID is required: the journey only runs against the local synthetic clinic."));
        var prefix = $"Hospital:Tenants:{tenant:D}:";
        var settings = HospitalConnectionStore.Defaults(tenant, env).ToDictionary(x => prefix + x.Key, x => x.Value);
        foreach (var pair in env.GetSection(prefix.TrimEnd(':')).GetChildren()) settings[prefix + pair.Key] = pair.Value;
        Assert.True(settings.ContainsKey(prefix + "BaseUrl"), "Hospital connection is not configured in .env");
        settings[prefix + "AccessToken"] = ""; // the harness default is a synthetic token; here the service account signs in for real
        settings["NVIDIA_API_KEY"] = env["NVIDIA_API_KEY"]; settings["AI_MODEL"] = env["AI_MODEL"] ?? AgentRuntime.DefaultModel; settings["AI_BASE_URL"] = env["AI_BASE_URL"];
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        // A number nobody owns in this installation, and a name no earlier run used (Hospital detects duplicates by name and birth date).
        var suffix = new string(Enumerable.Range(0, 6).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
        var family = "Prueba " + char.ToUpperInvariant(suffix[0]) + suffix[1..];
        await using var h = new AgentHarness(tenant, "50300" + Random.Shared.Next(100000, 999999));
        await h.Start(AgentEvals.Guide, "Hola, soy paciente nuevo y quiero una cita lo más pronto posible.");
        (await h.Db.Tenants.SingleAsync(t => t.Id == tenant)).EmergencyPhone = "2200 0000"; await h.Db.SaveChangesAsync();

        using var modelHttp = new SocketsHttpHandler(); using var hospitalHttp = new SocketsHttpHandler();
        var hospital = new HospitalClient(new HttpClient(hospitalHttp), config);
        async Task<string> Turn(string? message)
        {
            if (message is not null) await h.Say("patient", message);
            var before = h.Sent.Count;
            await h.Runtime(modelHttp, h.Sender(), hospitalHttp, settings).Run(h.Job, new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token);
            var reply = string.Join("\n", h.Sent.Skip(before));
            output.WriteLine($"PACIENTE: {message ?? "(primer mensaje)"}\nAGENTE: {reply}\n");
            if ((await h.Fresh()) is { Status: "human" } paused) output.WriteLine("DERIVADA: " + paused.Summary + "\n");
            return reply;
        }
        // What tapping does: the webhook turns the tapped button or row into the text the conversation stores.
        string Tap(Func<System.Text.Json.JsonElement, System.Text.Json.JsonElement> pick, string kind)
        {
            var option = pick(h.Interactive[^1]); var reply = kind == "button_reply" ? option.GetProperty("reply") : option;
            return WhatsAppContent.Inbound(System.Text.Json.JsonSerializer.SerializeToElement(new { type = "interactive", interactive = new Dictionary<string, object> { ["type"] = kind, [kind] = new { id = reply.GetProperty("id").GetString(), title = reply.GetProperty("title").GetString() } } }), default);
        }
        string TapConfirm() => Tap(i => i.GetProperty("action").GetProperty("buttons")[0], "button_reply");

        // 1. The hour first (agent-slot-first): the agent reads the agenda, the patient taps the first free hour and the form asks for the data.
        await Turn(null);
        Assert.Equal("list", h.Interactive[^1].GetProperty("type").GetString());
        Assert.Contains("son 7 datos cortos", await Turn(Tap(i => i.GetProperty("action").GetProperty("sections")[0].GetProperty("rows")[0], "list_reply")));
        var offer = ""; foreach (var answer in new[] { "Ana Sintética", family, "12/03/1990", "Femenino", "Carlos Sintético", "Hermano", "7000 0001" }) offer = await Turn(answer);
        Assert.Contains($"*Ana Sintética {family}*\nNacimiento: 12 de marzo de 1990\nSexo: femenino", offer);
        Assert.Null(h.Contact.PatientId);

        // 2. One card, one Confirmar: the record and the appointment.
        var booked = await Turn(TapConfirm());
        await h.Db.Entry(h.Contact).ReloadAsync();
        var patient = Assert.NotNull(h.Contact.PatientId);
        Assert.Contains("ya te registré", booked);
        var record = await hospital.GetVerifiedPatientAsync(tenant, patient, h.Contact.Phone); // Hospital really holds the record, with this phone
        Assert.Equal(family, record.FamilyNames);
        Assert.Contains("quedó agendada", booked);
        Assert.Equal("agent", (await h.Fresh()).Status);
        var summary = Regex.Match(offer, @"\*Cita:\* \w+ (\d+) de (\w+)(?: de (\d{4}))? a las (\d{2}):(\d{2})"); Assert.True(summary.Success, "No server summary in: " + offer);
        var today = HospitalClient.ClinicalDay(DateTimeOffset.UtcNow, "America/El_Salvador");
        var agenda = await hospital.GetPatientAppointmentRangeAsync(tenant, patient, h.Contact.Phone, today, today.AddDays(20));
        var row = Assert.Single(agenda.GetProperty("rows").EnumerateArray());
        var start = TimeZoneInfo.ConvertTime(row.GetProperty("scheduledStart").GetDateTimeOffset(), TimeZoneInfo.FindSystemTimeZoneById("America/El_Salvador"));
        output.WriteLine($"HOSPITAL: cita {row.GetProperty("status").GetString()} el {start:yyyy-MM-dd HH:mm} con {row.GetProperty("clinicianName").GetString()}\n");
        Assert.Equal("booked", row.GetProperty("status").GetString());
        Assert.Equal((int.Parse(summary.Groups[1].Value), int.Parse(summary.Groups[4].Value), int.Parse(summary.Groups[5].Value)), (start.Day, start.Hour, start.Minute)); // what the patient confirmed is what Hospital holds

        // 3. A frequent question, then the appointment the agent just made.
        Assert.Matches(@"\b8(:00)?\b", await Turn("¿A qué hora abren los sábados?"));
        Assert.Contains(start.ToString("HH:mm"), await Turn($"¿Qué cita tengo el {start:yyyy-MM-dd}?"));

        // The journey leaves no slot taken behind it: the appointment is cancelled the way a patient would cancel it.
        await hospital.CancelAppointmentAsync(tenant, patient, h.Contact.Phone, row.GetProperty("appointmentId").GetGuid());
        var after = await hospital.GetPatientAppointmentRangeAsync(tenant, patient, h.Contact.Phone, today, today.AddDays(20));
        Assert.StartsWith("cancelled", Assert.Single(after.GetProperty("rows").EnumerateArray()).GetProperty("status").GetString());
        output.WriteLine("HOSPITAL: cita de prueba cancelada, horario liberado\n");

        // 4. An emergency goes to a person, with the hospital's number.
        var urgent = await Turn("Tengo fiebre muy alta y necesito una cita de emergencia.");
        Assert.Contains("2200 0000", urgent);
        Assert.Equal("human", (await h.Fresh()).Status);
    }

    /// <summary>What the agent reads about the hospital itself, against the LOCAL Hospital: its roll with disciplines, its catalogue and its
    /// published contact. The client turns a failure of the first and the last into "nothing known", so only a non-empty roll proves the route.</summary>
    [Fact, Trait("Category", "Live")]
    public async Task HospitalAnswersItsDisciplinesCatalogueAndContact()
    {
        var env = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var tenant = Guid.Parse(env["DEV_HOSPITAL_TENANT_ID"] ?? throw new InvalidOperationException("DEV_HOSPITAL_TENANT_ID is required."));
        var prefix = $"Hospital:Tenants:{tenant:D}:";
        var settings = HospitalConnectionStore.Defaults(tenant, env).ToDictionary(x => prefix + x.Key, x => x.Value);
        foreach (var pair in env.GetSection(prefix.TrimEnd(':')).GetChildren()) settings[prefix + pair.Key] = pair.Value;
        settings[prefix + "AccessToken"] = "";
        var hospital = new HospitalClient(new HttpClient(), new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        var disciplines = await hospital.ClinicianDisciplines(tenant);
        output.WriteLine("DISCIPLINAS: " + string.Join(", ", disciplines.Values.GroupBy(d => d).Select(g => $"{g.Key} ×{g.Count()}")));
        Assert.NotEmpty(disciplines); Assert.All(disciplines.Values, d => Assert.Contains(d, new[] { "medicine", "dentistry", "nutrition" }));

        var contact = await hospital.ClinicContact(tenant);
        output.WriteLine(contact is null ? "CONTACTO: el hospital no publicó enlace (o la lectura falló)" : $"CONTACTO: dirección {(contact.PublicAddress is { Length: > 0 } ? "sí" : "no")}, teléfono {(contact.PublicPhone is { Length: > 0 } ? "sí" : "no")}");

        var services = await hospital.Services(tenant, null, 1);
        output.WriteLine($"SERVICIOS: {services.Items.Count} activos, {services.Items.Count(s => s.Price > 0)} con precio, disciplinas: {string.Join(", ", services.Items.Select(s => s.Discipline ?? "—").Distinct())}");
    }

    /// <summary>The real agent and model answering from the LOCAL Hospital's own configuration, with prices switched on: the list price of
    /// a service in its catalogue, its published address, whether it has a dentist and the discount of a company the patient names.
    /// AGENT_EVAL_PROVIDER=openai keeps it off the NIM key. Needs one active company with an active agreement in that Hospital.</summary>
    [Fact, Trait("Category", "Live")]
    public async Task AgentAnswersPriceAddressDisciplineAndAgreementFromHospital()
    {
        var (tenant, settings, hospital) = ModelSettings();
        var service = (await hospital.Services(tenant, null, 1)).Items.First(s => s.Price > 0 && s.Currency == "USD");
        var contact = Assert.IsType<HospitalClinicContact>(await hospital.ClinicContact(tenant));
        Assert.Contains("dentistry", (await hospital.ClinicianDisciplines(tenant)).Values);
        var company = (await hospital.Companies(tenant, null, 1)).Items.FirstOrDefault(c => c.Active && c.Agreement is { Active: true, DiscountPercent: > 0 })
            ?? throw new InvalidOperationException("The local Hospital has no active company with an active agreement: create one in Empresas y servicios.");

        var (h, turn, tools) = await Conversation(tenant, settings, "on", $"Hola, ¿cuánto cuesta {service.Name}?");
        await using var _ = h;
        Assert.Contains(((int)service.Price).ToString(), await turn(null));
        Assert.Contains(contact.PublicAddress!.Split(' ').MaxBy(word => word.Length)!.Trim(',', '.'), await turn("¿Y cuál es la dirección del hospital?"), StringComparison.OrdinalIgnoreCase);
        Assert.Matches("(?i)odont|dentista", await turn("¿Tienen dentista?"));
        Assert.Contains(((int)company.Agreement!.DiscountPercent).ToString(), await turn($"Trabajo en {company.Name}, ¿tengo algún descuento?"));
        var used = await tools();
        Assert.Contains("hospital_services", used); Assert.Contains("hospital_contact", used); Assert.Contains("hospital_agreement", used);
    }

    /// <summary>The same hospital with prices left off (the default): the agent confirms the service exists and never says its price.</summary>
    [Fact, Trait("Category", "Live")]
    public async Task AgentKeepsThePriceToReceptionWhenPricesAreOff()
    {
        var (tenant, settings, hospital) = ModelSettings();
        var service = (await hospital.Services(tenant, null, 1)).Items.First(s => s.Price > 0 && s.Currency == "USD");
        var (h, turn, tools) = await Conversation(tenant, settings, "off", $"Hola, ¿cuánto cuesta {service.Name}?");
        await using var _ = h;
        var reply = await turn(null);
        Assert.DoesNotContain(((int)service.Price).ToString(), reply);
        Assert.Contains(service.Name, reply, StringComparison.OrdinalIgnoreCase); // «confirma si ofreces el servicio», as the setting promises
        Assert.Contains("hospital_services", await tools());
    }

    /// <summary>A registered patient says the appointment is to collect results, taps a free hour from the list and confirms: Hospital books it
    /// as «results», not as an ordinary follow-up. The appointment is cancelled at the end.</summary>
    [Fact, Trait("Category", "Live")]
    public async Task TappedHourKeepsWhatThePatientSaidTheAppointmentIsFor()
    {
        var (tenant, settings, hospital) = ModelSettings();
        var suffix = new string(Enumerable.Range(0, 6).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
        var (h, turn, _) = await Conversation(tenant, settings, "off", "Hola, necesito una cita para que me entreguen mis resultados de laboratorio. ¿Qué horarios tienen?");
        await using var __ = h;
        var registered = await hospital.RegisterPatientAsync(tenant, new("Ana Sintética", "Resultados " + char.ToUpperInvariant(suffix[0]) + suffix[1..], new DateOnly(1990, 3, 12), "female", h.Contact.Phone, "Carlos Sintético", "hermano", "70000001"), "live:" + Guid.NewGuid());
        var patient = Assert.NotNull(registered.PatientId); h.Contact.PatientId = patient; await h.Db.SaveChangesAsync();

        // What tapping does: the first option of whatever the agent sent (doctor, morning or afternoon, an hour, Confirmar), as the webhook stores it.
        string TapFirst()
        {
            var action = h.Interactive[^1].GetProperty("action");
            var (kind, option) = action.TryGetProperty("buttons", out var buttons) ? ("button_reply", buttons[0].GetProperty("reply")) : ("list_reply", action.GetProperty("sections")[0].GetProperty("rows")[0]);
            return WhatsAppContent.Inbound(System.Text.Json.JsonSerializer.SerializeToElement(new { type = "interactive", interactive = new Dictionary<string, object> { ["type"] = kind, [kind] = new { id = option.GetProperty("id").GetString(), title = option.GetProperty("title").GetString() } } }), default);
        }
        var reply = await turn(null); var card = "";
        for (var taps = 0; !reply.Contains("quedó agendada") && taps < 6; taps++) { if (reply.StartsWith("*Cita:*")) card = reply; reply = await turn(TapFirst()); }
        Assert.Contains("quedó agendada", reply);

        var today = HospitalClient.ClinicalDay(DateTimeOffset.UtcNow, "America/El_Salvador");
        var booked = Assert.Single((await hospital.GetPatientAppointmentRangeAsync(tenant, patient, h.Contact.Phone, today, today.AddDays(20))).GetProperty("rows").EnumerateArray());
        // Hospital's patient read does not return the visit kind; the card is built from the same proposal Hospital is booked from.
        output.WriteLine("HOSPITAL: cita " + booked.GetProperty("appointmentId").GetGuid());
        try { Assert.Contains("*Motivo:* Entrega de resultados", card); }
        finally { await hospital.CancelAppointmentAsync(tenant, patient, h.Contact.Phone, booked.GetProperty("appointmentId").GetGuid()); }
    }

    static (Guid Tenant, Dictionary<string, string?> Settings, HospitalClient Hospital) ModelSettings()
    {
        var env = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var tenant = Guid.Parse(env["DEV_HOSPITAL_TENANT_ID"] ?? throw new InvalidOperationException("DEV_HOSPITAL_TENANT_ID is required."));
        var prefix = $"Hospital:Tenants:{tenant:D}:";
        var settings = HospitalConnectionStore.Defaults(tenant, env).ToDictionary(x => prefix + x.Key, x => x.Value);
        foreach (var pair in env.GetSection(prefix.TrimEnd(':')).GetChildren()) settings[prefix + pair.Key] = pair.Value;
        settings[prefix + "AccessToken"] = "";
        var openAi = env["AGENT_EVAL_PROVIDER"] == "openai";
        settings["NVIDIA_API_KEY"] = env[openAi ? "OPENAI_API_KEY" : "NVIDIA_API_KEY"]; settings["AI_BASE_URL"] = openAi ? "https://api.openai.com/v1" : env["AI_BASE_URL"];
        settings["AI_MODEL"] = env["AGENT_EVAL_MODEL"] is { Length: > 0 } chosen ? chosen : env[openAi ? "OPENAI_MODEL" : "AI_MODEL"] ?? AgentRuntime.DefaultModel;
        settings["OPENAI_REASONING_EFFORT"] = env["AGENT_EVAL_REASONING"] ?? env["OPENAI_REASONING_EFFORT"];
        return (tenant, settings, new HospitalClient(new HttpClient(), new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
    }

    /// <summary>A new conversation with the hospital's prices choice; each turn runs the real agent and must not hand the patient over.</summary>
    async Task<(AgentHarness, Func<string?, Task<string>>, Func<Task<List<string>>>)> Conversation(Guid tenant, Dictionary<string, string?> settings, string prices, string first)
    {
        var h = new AgentHarness(tenant, "50300" + Random.Shared.Next(100000, 999999));
        await h.Start(AgentEvals.Guide, first);
        (await h.Db.Tenants.SingleAsync(t => t.Id == tenant)).Attention = new Attention { Prices = prices }.Checked().Write(); await h.Db.SaveChangesAsync();
        var modelHttp = new SocketsHttpHandler(); var hospitalHttp = new SocketsHttpHandler();
        async Task<string> Turn(string? message)
        {
            if (message is not null) await h.Say("patient", message);
            var before = h.Sent.Count;
            await h.Runtime(modelHttp, h.Sender(), hospitalHttp, settings).Run(h.Job, new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token);
            var reply = string.Join("\n", h.Sent.Skip(before));
            output.WriteLine($"PACIENTE: {message ?? first}\nAGENTE: {reply}\n");
            foreach (var held in await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id && a.Kind == "guard").Select(a => a.Body).ToListAsync()) output.WriteLine("GUARDA: " + held);
            Assert.NotEqual("human", (await h.Fresh()).Status);
            return reply;
        }
        async Task<List<string>> Tools()
        {
            var used = (await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id && a.Kind == "agent_tool").Select(a => a.Body).ToListAsync()).Select(body => Regex.Match(body, @"Herramienta: (\w+)\.").Groups[1].Value).ToList();
            output.WriteLine("HERRAMIENTAS: " + string.Join(", ", used));
            return used;
        }
        return (h, Turn, Tools);
    }

    /// <summary>The part of booking that needs no model: a registered patient taps a free hour, taps Confirmar, and the
    /// appointment exists in the LOCAL Hospital. It keeps working while the AI provider is down or rate-limited.</summary>
    [Fact, Trait("Category", "Live")]
    public async Task TappedSlotBooksInHospitalWithoutTheModel()
    {
        var env = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var tenant = Guid.Parse(env["DEV_HOSPITAL_TENANT_ID"] ?? throw new InvalidOperationException("DEV_HOSPITAL_TENANT_ID is required."));
        var prefix = $"Hospital:Tenants:{tenant:D}:";
        var settings = HospitalConnectionStore.Defaults(tenant, env).ToDictionary(x => prefix + x.Key, x => x.Value);
        foreach (var pair in env.GetSection(prefix.TrimEnd(':')).GetChildren()) settings[prefix + pair.Key] = pair.Value;
        settings[prefix + "AccessToken"] = "";
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var suffix = new string(Enumerable.Range(0, 6).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
        await using var h = new AgentHarness(tenant, "50300" + Random.Shared.Next(100000, 999999)); await h.Start(AgentEvals.Guide);
        using var hospitalHttp = new SocketsHttpHandler(); var hospital = new HospitalClient(new HttpClient(hospitalHttp), config);
        var noModel = new AgentHarness.Fake(_ => throw new InvalidOperationException("The model must not be consulted"));

        var registered = await hospital.RegisterPatientAsync(tenant, new("Ana Sintética", "Toque " + char.ToUpperInvariant(suffix[0]) + suffix[1..], new DateOnly(1990, 3, 12), "female", h.Contact.Phone, "Carlos Sintético", "hermano", "70000001"), "live:" + Guid.NewGuid());
        var patient = Assert.NotNull(registered.PatientId); h.Contact.PatientId = patient; await h.Db.SaveChangesAsync();

        // The same row the agent's list would carry for the hospital's first free hour.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/El_Salvador"); var today = HospitalClient.ClinicalDay(DateTimeOffset.UtcNow, "America/El_Salvador");
        var options = await hospital.GetAvailabilityAsync(tenant, today, today.AddDays(13));
        var first = options.Professionals.SelectMany(p => p.Days.SelectMany(d => d.Slots).Where(slot => slot.Offered && slot.TakenBy == 0 && slot.StartsAt > DateTimeOffset.UtcNow).Select(slot => (Doctor: p, Slot: slot))).OrderBy(x => x.Slot.StartsAt).First();
        var local = TimeZoneInfo.ConvertTime(first.Slot.StartsAt, zone);
        await h.Say("patient", $"CITA {local:yyyy-MM-ddTHH:mm:sszzz} {first.Doctor.ClinicianId:D} {first.Slot.DurationMinutes} {first.Doctor.ClinicianName}");

        await h.Runtime(noModel, h.Sender(), hospitalHttp, settings).Run(h.Job, CancellationToken.None);
        output.WriteLine("PACIENTE toca un horario\nAGENTE: " + h.Sent[^1] + "\n");
        var confirm = h.Interactive[^1].GetProperty("action").GetProperty("buttons")[0].GetProperty("reply").GetProperty("id").GetString()!;
        Assert.StartsWith("CONFIRMAR ", confirm);

        await h.Say("patient", confirm);
        await h.Runtime(noModel, h.Sender(), hospitalHttp, settings).Run(h.Job, CancellationToken.None);
        output.WriteLine("PACIENTE toca Confirmar\nAGENTE: " + h.Sent[^1] + "\n");
        if ((await h.Fresh()) is { Status: "human" } paused) output.WriteLine("DERIVADA: " + paused.Summary);
        Assert.Contains("quedó agendada", h.Sent[^1]); Assert.Contains($"a las {local:HH:mm}", h.Sent[^1]);

        var row = Assert.Single((await hospital.GetPatientAppointmentRangeAsync(tenant, patient, h.Contact.Phone, today, today.AddDays(20))).GetProperty("rows").EnumerateArray());
        Assert.Equal("booked", row.GetProperty("status").GetString());
        Assert.Equal(first.Slot.StartsAt, row.GetProperty("scheduledStart").GetDateTimeOffset());
        output.WriteLine($"HOSPITAL: cita {row.GetProperty("status").GetString()} el {local:yyyy-MM-dd HH:mm} con {row.GetProperty("clinicianName").GetString()}");

        await hospital.CancelAppointmentAsync(tenant, patient, h.Contact.Phone, row.GetProperty("appointmentId").GetGuid());
        Assert.StartsWith("cancelled", Assert.Single((await hospital.GetPatientAppointmentRangeAsync(tenant, patient, h.Contact.Phone, today, today.AddDays(20))).GetProperty("rows").EnumerateArray()).GetProperty("status").GetString());
        output.WriteLine("HOSPITAL: cita de prueba cancelada, horario liberado");
    }

    /// <summary>The whole path of a new client with no model at all: menu, registration form, free hours, booking.
    /// This is what keeps working while the AI provider is rate-limited.</summary>
    [Fact, Trait("Category", "Live")]
    public async Task NewClientRegistersAndBooksFromTheMenuWithoutTheModel()
    {
        var env = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var tenant = Guid.Parse(env["DEV_HOSPITAL_TENANT_ID"] ?? throw new InvalidOperationException("DEV_HOSPITAL_TENANT_ID is required."));
        var prefix = $"Hospital:Tenants:{tenant:D}:";
        var settings = HospitalConnectionStore.Defaults(tenant, env).ToDictionary(x => prefix + x.Key, x => x.Value);
        foreach (var pair in env.GetSection(prefix.TrimEnd(':')).GetChildren()) settings[prefix + pair.Key] = pair.Value;
        settings[prefix + "AccessToken"] = "";
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var suffix = new string(Enumerable.Range(0, 6).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
        var family = "Menú " + char.ToUpperInvariant(suffix[0]) + suffix[1..];
        await using var h = new AgentHarness(tenant, "50300" + Random.Shared.Next(100000, 999999)); await h.Start(AgentEvals.Guide, "Hola");
        using var hospitalHttp = new SocketsHttpHandler(); var hospital = new HospitalClient(new HttpClient(hospitalHttp), config);
        var noModel = new AgentHarness.Fake(_ => throw new InvalidOperationException("The model must not be consulted"));
        async Task<string> Turn(string? message)
        {
            if (message is not null) await h.Say("patient", message);
            var before = h.Sent.Count;
            await h.Runtime(noModel, h.Sender(), hospitalHttp, settings).Run(h.Job, CancellationToken.None);
            var reply = string.Join("\n", h.Sent.Skip(before));
            output.WriteLine($"PACIENTE: {message ?? "Hola"}\nAGENTE: {reply}\n");
            return reply;
        }
        string Button(int index) => h.Interactive[^1].GetProperty("action").GetProperty("buttons")[index].GetProperty("reply").GetProperty("id").GetString()!;

        await Turn(null);
        Assert.Equal("AGENDAR", AgentHarness.Options(h.Interactive[^1])[0].Id);
        await Turn("AGENDAR"); // the hour first (agent-slot-first): the free hours, then the form for the one tapped
        Assert.Contains("son 7 datos cortos", await Turn(h.Interactive[^1].GetProperty("action").GetProperty("sections")[0].GetProperty("rows")[0].GetProperty("id").GetString()));
        string reply = ""; foreach (var answer in new[] { "Ana Sintética", family, "12/03/1990", "Femenino", "Carlos Sintético", "Hermano", "7000 0001" }) reply = await Turn(answer);
        Assert.Contains($"*Ana Sintética {family}*", reply); Assert.Contains("*Cita:*", reply);

        var booked = await Turn(Button(0)); // one Confirmar: the record and the appointment
        await h.Db.Entry(h.Contact).ReloadAsync();
        var patient = Assert.NotNull(h.Contact.PatientId);
        Assert.Contains("ya te registré", booked);
        Assert.Contains("quedó agendada", booked);

        var today = HospitalClient.ClinicalDay(DateTimeOffset.UtcNow, "America/El_Salvador");
        var appointment = Assert.Single((await hospital.GetPatientAppointmentRangeAsync(tenant, patient, h.Contact.Phone, today, today.AddDays(20))).GetProperty("rows").EnumerateArray());
        Assert.Equal("booked", appointment.GetProperty("status").GetString());
        output.WriteLine($"HOSPITAL: cita {appointment.GetProperty("status").GetString()} con {appointment.GetProperty("clinicianName").GetString()}\n");
        var id = appointment.GetProperty("appointmentId").GetGuid(); var original = appointment.GetProperty("scheduledStart").GetDateTimeOffset();
        async Task<System.Text.Json.JsonElement> InHospital() => (await hospital.GetPatientAppointmentRangeAsync(tenant, patient, h.Contact.Phone, today, today.AddDays(20))).GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("appointmentId").GetGuid() == id).Clone();

        // «Mis citas»: the patient moves the appointment and then cancels it, tapping, with no model. It is also how this journey frees its slot.
        await Turn("MISCITAS");
        Assert.Equal([$"MOVER {id}", $"CANCELAR {id}", "MENU"], AgentHarness.Options(h.Interactive[^1]).Select(o => o.Id));
        await Turn($"MOVER {id}");
        await Turn(AgentHarness.Options(h.Interactive[^1])[0].Id);
        Assert.Contains("quedó reprogramada", await Turn("Sí"));
        var moved = await InHospital();
        Assert.Equal("booked", moved.GetProperty("status").GetString()); Assert.NotEqual(original, moved.GetProperty("scheduledStart").GetDateTimeOffset());
        output.WriteLine($"HOSPITAL: cita movida a {TimeZoneInfo.ConvertTime(moved.GetProperty("scheduledStart").GetDateTimeOffset(), TimeZoneInfo.FindSystemTimeZoneById("America/El_Salvador")):yyyy-MM-dd HH:mm}\n");

        await Turn($"CANCELAR {id}");
        Assert.Contains("quedó cancelada", await Turn(Button(0)));
        Assert.StartsWith("cancelled", (await InHospital()).GetProperty("status").GetString());
        output.WriteLine("HOSPITAL: cita cancelada por el paciente, horario liberado");
    }
}
