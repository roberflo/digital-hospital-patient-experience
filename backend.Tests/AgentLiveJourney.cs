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
        (await h.Db.Tenants.SingleAsync(t => t.Id == tenant)).EmergencyPhone = "+503 77372990"; await h.Db.SaveChangesAsync();

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
        static string Code(string reply) => Regex.Match(reply, "CONFIRMAR ([0-9A-F]{6})") is { Success: true } m ? m.Groups[1].Value : throw new Xunit.Sdk.XunitException("No confirmation code in: " + reply);

        // 1. Registration.
        var asked = await Turn(null);
        Assert.Matches("(?i)nacimiento", asked);
        var proposal = await Turn($"Me llamo Ana Sintética {family}. Nombres: Ana Sintética. Apellidos: {family}. Nací el 12 de marzo de 1990. Sexo femenino. Mi contacto de emergencia es Carlos Sintético, mi hermano, teléfono 70000001.");
        Assert.Contains($"Registro: Ana Sintética {family}, nacimiento 12 de marzo de 1990, sexo femenino", proposal);
        Assert.Null(h.Contact.PatientId);
        var registered = await Turn("CONFIRMAR " + Code(proposal));
        await h.Db.Entry(h.Contact).ReloadAsync();
        var patient = Assert.NotNull(h.Contact.PatientId);
        Assert.Contains("registrado", registered);
        var record = await hospital.GetVerifiedPatientAsync(tenant, patient, h.Contact.Phone); // Hospital really holds the record, with this phone
        Assert.Equal(family, record.FamilyNames);

        // 2. Booking the first free slot.
        var offer = await Turn("Quiero la primera cita disponible, con cualquier doctor.");
        var booked = await Turn("CONFIRMAR " + Code(offer));
        Assert.Contains("confirmó", booked);
        Assert.Equal("agent", (await h.Fresh()).Status);
        var summary = Regex.Match(offer, @"Cita: \w+ (\d+) de (\w+) de (\d{4}) a las (\d{2}):(\d{2})\."); Assert.True(summary.Success, "No server summary in: " + offer);
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

        // 4. An emergency goes to a person, with the hospital's number.
        var urgent = await Turn("Tengo fiebre muy alta y necesito una cita de emergencia.");
        Assert.Contains("+503 77372990", urgent);
        Assert.Equal("human", (await h.Fresh()).Status);
    }
}
