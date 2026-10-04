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

        // 1. Registration.
        var asked = await Turn(null);
        Assert.Matches("(?i)nacimiento", asked);
        var proposal = await Turn($"Me llamo Ana Sintética {family}. Nombres: Ana Sintética. Apellidos: {family}. Nací el 12 de marzo de 1990. Sexo femenino. Mi contacto de emergencia es Carlos Sintético, mi hermano, teléfono 70000001.");
        Assert.Contains($"*Ana Sintética {family}*\nNacimiento: 12 de marzo de 1990\nSexo: femenino", proposal);
        Assert.Null(h.Contact.PatientId);
        var registered = await Turn(TapConfirm());
        await h.Db.Entry(h.Contact).ReloadAsync();
        var patient = Assert.NotNull(h.Contact.PatientId);
        Assert.Contains("ya tienes tu expediente", registered);
        var record = await hospital.GetVerifiedPatientAsync(tenant, patient, h.Contact.Phone); // Hospital really holds the record, with this phone
        Assert.Equal(family, record.FamilyNames);

        // 2. Booking the first free slot.
        var offer = await Turn("Quiero la primera cita disponible, con cualquier doctor.");
        // If the agent listed the free hours, the patient taps the first one; if it proposed directly, there is already a Confirmar button.
        if (h.Interactive[^1].GetProperty("type").GetString() == "list") offer = await Turn(Tap(i => i.GetProperty("action").GetProperty("sections")[0].GetProperty("rows")[0], "list_reply"));
        var booked = await Turn(TapConfirm());
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

        var registered = await hospital.RegisterPatientAsync(tenant, new("Ana Sintética", "Toque " + char.ToUpperInvariant(suffix[0]) + suffix[1..], new DateOnly(1990, 3, 12), "female", h.Contact.Phone, "Carlos Sintético", "hermano", "70000001"));
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
        Assert.Contains("Paso 1 de 7", await Turn("AGENDAR"));
        string reply = ""; foreach (var answer in new[] { "Ana Sintética", family, "12/03/1990", "Femenino", "Carlos Sintético", "Hermano", "7000 0001" }) reply = await Turn(answer);
        Assert.Contains($"*Ana Sintética {family}*", reply);

        var registered = await Turn(Button(0));
        await h.Db.Entry(h.Contact).ReloadAsync();
        var patient = Assert.NotNull(h.Contact.PatientId);
        Assert.Contains("ya tienes tu expediente", registered);
        var row = h.Interactive[^1].GetProperty("action").GetProperty("sections")[0].GetProperty("rows")[0];

        await Turn(row.GetProperty("id").GetString());
        var booked = await Turn(Button(0));
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
