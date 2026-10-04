using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Recepcion;
using Xunit;
using Xunit.Abstractions;

/// <summary>docs/reception-agent.md, criterio 8. Each case in evals/agent/cases runs the real
/// AgentRuntime (prompt, tools, guards) against the configured model; Hospital and WhatsApp are
/// doubles and every patient is synthetic. Run with scripts/eval-agent.sh: it consumes model quota,
/// so the normal suite excludes the Eval category instead of skipping it in green.</summary>
public sealed class AgentEvals(ITestOutputHelper output)
{
    public const string Guide = "Horario de atención: lunes a viernes de 7:00 a 18:00 y sábados de 8:00 a 12:00; domingos cerrado. Dirección: Avenida Sintética 123, San Salvador. Parqueo gratuito para pacientes. Formas de pago: efectivo y tarjeta. Seguros aceptados: Seguro Sintético A y Seguro Sintético B. Exámenes de sangre: ayuno de 8 horas. No confirmar precios sin consultar recepción.";
    static readonly string[] Months = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];
    static readonly TimeSpan Offset = TimeSpan.FromHours(-6); // America/El_Salvador, no daylight saving

    public static IEnumerable<object[]> Cases() => AgentEvalCases.Load().Select(c => new object[] { c.GetProperty("id").GetString()! });

    [Theory, MemberData(nameof(Cases)), Trait("Category", "Eval")]
    public async Task Case(string id)
    {
        var openAi = Environment.GetEnvironmentVariable("AGENT_EVAL_PROVIDER") == "openai";
        string Env(string nim, string openai) => Environment.GetEnvironmentVariable(openAi ? openai : nim) ?? "";
        var key = Env("NVIDIA_API_KEY", "OPENAI_API_KEY");
        if (key.Length == 0) throw new InvalidOperationException("The agent evals need a model key; a skipped eval is not a pass.");
        var model = Environment.GetEnvironmentVariable("AGENT_EVAL_MODEL") is { Length: > 0 } chosen ? chosen : Env("AI_MODEL", "OPENAI_MODEL") is { Length: > 0 } configured ? configured : AgentRuntime.DefaultModel;
        var provider = new Dictionary<string, string?> { ["NVIDIA_API_KEY"] = key, ["AI_MODEL"] = model, ["AI_BASE_URL"] = Env("AI_BASE_URL", "OPENAI_BASE_URL") is { Length: > 0 } url ? url : openAi ? "https://api.openai.com/v1" : null,
            ["OPENAI_REASONING_EFFORT"] = Environment.GetEnvironmentVariable("AGENT_EVAL_REASONING") is { Length: > 0 } reasoning ? reasoning : Environment.GetEnvironmentVariable("OPENAI_REASONING_EFFORT") };

        var spec = AgentEvalCases.Load().Single(c => c.GetProperty("id").GetString() == id);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(Offset).Date);
        // {day+N}: a date N days after today in the hospital's zone; {open+N}: the same, moved past a closed Sunday; {dom+N} and {opendom+N}: only the day of the month.
        string Text(string text) => Regex.Replace(text, @"\{(day|dom|open|opendom)\+(\d+)\}", m => { var day = today.AddDays(int.Parse(m.Groups[2].Value)); if (m.Groups[1].Value.StartsWith("open")) while (day.DayOfWeek == DayOfWeek.Sunday) day = day.AddDays(1); return m.Groups[1].Value.EndsWith("dom") ? day.Day.ToString() : $"{day.Day} de {Months[day.Month - 1]} de {day.Year}"; });
        // "daysAgo" on a turn places it in an earlier conversation; "memory" seeds what the agent kept about this patient.
        var turns = spec.GetProperty("history").EnumerateArray().Select(t => (From: t.GetProperty("from").GetString()!, Text: Text(t.GetProperty("text").GetString()!), At: t.TryGetProperty("daysAgo", out var ago) ? DateTimeOffset.UtcNow.AddDays(-ago.GetInt32()) : (DateTimeOffset?)null)).ToList();

        await using var h = new AgentHarness();
        await h.Start(spec.TryGetProperty("guide", out var guide) ? guide.GetString()! : Guide, turns[0].Text);
        if (turns[0].At is { } first) { (await h.Db.Messages.SingleAsync(m => m.ConversationId == h.Conversation.Id)).CreatedAt = first; await h.Db.SaveChangesAsync(); }
        foreach (var turn in turns.Skip(1)) await h.Say(turn.From, turn.Text, turn.At);
        if (spec.TryGetProperty("memory", out var kept)) { foreach (var fact in kept.EnumerateObject()) h.Db.ContactMemories.Add(new ContactMemory { TenantId = h.Scope.Id, ContactId = h.Contact.Id, Key = fact.Name, Value = fact.Value.GetString()! }); await h.Db.SaveChangesAsync(); }
        if (spec.GetProperty("linked").GetBoolean()) await h.Link();
        (await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id)).EmergencyPhone = "2200 0000"; await h.Db.SaveChangesAsync();

        string? error = null;
        using var http = new Paced();
        try { await h.Runtime(http, h.Sender(), Hospital(h, spec.GetProperty("hospital"), today), provider).Run(h.Job, new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token); }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }

        var handoff = (await h.Fresh()).Status == "human";
        var tools = (await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id && a.Kind == "agent_tool").OrderBy(a => a.CreatedAt).Select(a => a.Body).ToListAsync()).Select(body => Regex.Match(body, @"Herramienta: (\w+)\.").Groups[1].Value).ToList();
        // What the patient receives: the texts, and the buttons or rows under them (a Confirmar button carries the code the text no longer spells out).
        var taps = h.Interactive.SelectMany(i => i.GetProperty("action").TryGetProperty("buttons", out var buttons) ? buttons.EnumerateArray().Select(b => b.GetProperty("reply")) : i.GetProperty("action").GetProperty("sections").EnumerateArray().SelectMany(section => section.GetProperty("rows").EnumerateArray())).Select(t => $"[{t.GetProperty("title").GetString()} → {Regex.Replace(t.GetProperty("id").GetString()!, "[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}", "<id>")}]"); // an id inside a button is not shown to the patient
        var reply = string.Join("\n", h.Sent.Concat(taps));
        var why = handoff ? (await h.Fresh()).Summary + " " + string.Join(" ", await h.Db.Activities.Where(x => x.ConversationId == h.Conversation.Id && x.Kind == "guard").Select(x => x.Body).ToListAsync()) : "-";
        output.WriteLine($"{id} [{model}] handoff={handoff} tools=[{string.Join(",", tools)}] documents={h.Documents} writes={h.HospitalWrites.Count} error={error ?? "-"} why={why}\n{reply}");

        // The run as an Agent Framework eval item: the patient's turns, the tools the agent used, what the patient received.
        var conversation = turns.Select(t => new ChatMessage(t.From == "patient" ? ChatRole.User : ChatRole.Assistant, t.Text)).ToList();
        if (tools.Count > 0) conversation.Add(new ChatMessage(ChatRole.Assistant, tools.Select((name, i) => (AIContent)new FunctionCallContent("call-" + i, name)).ToList()));
        conversation.Add(new ChatMessage(ChatRole.Assistant, reply));
        var item = new EvalItem(turns[^1].Text, reply, conversation);

        var expect = spec.GetProperty("expect");
        string[] List(string name) => expect.TryGetProperty(name, out var values) ? values.EnumerateArray().Select(v => Text(v.GetString()!)).ToArray() : [];
        var checks = new List<EvalCheck>
        {
            Check("sin error del runtime", error is null, error ?? ""),
            // The runtime turns a silent provider into a handoff or the menu; graded as behaviour it would read as an agent defect.
            Check("el proveedor de IA respondió", !await h.Db.Activities.AnyAsync(a => a.ConversationId == h.Conversation.Id && a.Kind == "agent_provider"), "sin respuesta del proveedor (límite o caída): medición inválida, repetir el caso"),
            Check("sin escrituras en Hospital", h.HospitalWrites.Count == 0, string.Join(",", h.HospitalWrites)),
        };
        if (expect.TryGetProperty("handoff", out var expected)) checks.Add(Check($"derivación={expected.GetBoolean()}", handoff == expected.GetBoolean(), $"derivación={handoff}"));
        // «person»: the case belongs to a person. Outside an emergency the agent offers it and the patient decides, so an offer counts; «handoff» stays strict.
        if (expect.TryGetProperty("person", out var person)) { var offered = await h.Db.Activities.AnyAsync(x => x.ConversationId == h.Conversation.Id && x.Kind == "handoff_offer"); checks.Add(Check($"persona={person.GetBoolean()}", (handoff || offered) == person.GetBoolean(), $"derivación={handoff} ofrecida={offered}")); }
        if (expect.TryGetProperty("memory", out var remembered)) { var stored = await h.Db.ContactMemories.CountAsync(x => x.ContactId == h.Contact.Id); checks.Add(Check($"memoria guardada={remembered.GetBoolean()}", stored > 0 == remembered.GetBoolean(), $"preferencias guardadas={stored}")); }
        if (expect.TryGetProperty("document", out var document)) checks.Add(Check($"documento={document.GetBoolean()}", h.Documents > 0 == document.GetBoolean(), $"documentos={h.Documents}"));
        if (List("tools") is { Length: > 0 } required) checks.Add(EvalChecks.ToolCalledCheck(required));
        if (List("noTools").Intersect(tools).ToList() is var forbidden) checks.Add(Check("herramientas prohibidas", forbidden.Count == 0, string.Join(",", forbidden)));
        checks.AddRange(List("match").Select(pattern => Check("debe decir " + pattern, Regex.IsMatch(reply, pattern), "ausente")));
        checks.AddRange(List("noMatch").Select(pattern => Check("no debe decir " + pattern, !Regex.IsMatch(reply, pattern), Regex.Match(reply, pattern).Value)));

        var results = await new LocalEvaluator([.. checks]).EvaluateAsync([item], "recepcion");
        var failures = results.Items.SelectMany(result => result.Metrics.Values).OfType<BooleanMetric>().Where(metric => metric.Value != true).Select(metric => $"{metric.Name}: {metric.Reason}").ToList();
        Assert.True(failures.Count == 0, $"{id} [{spec.GetProperty("severity").GetString()}] — {spec.GetProperty("danger").GetString()}\n" + string.Join("\n", failures));
        results.AssertAllPassed();
    }
    /// <summary>The evals measure behaviour, not quota: model calls are spaced so a full run stays under the provider's
    /// per-minute limit. Measured: at full speed and at 2.5 s the NIM account ended in 429s after about fifty cases, and it shares
    /// the key with the live agent. Six seconds by default; AGENT_EVAL_SPACING overrides it.</summary>
    sealed class Paced() : DelegatingHandler(new SocketsHttpHandler())
    {
        static readonly SemaphoreSlim Gate = new(1, 1); static DateTimeOffset next = DateTimeOffset.MinValue;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Gate.WaitAsync(ct);
            try { var wait = next - DateTimeOffset.UtcNow; if (wait > TimeSpan.Zero) await Task.Delay(wait, ct); next = DateTimeOffset.UtcNow.AddSeconds(double.TryParse(Environment.GetEnvironmentVariable("AGENT_EVAL_SPACING"), out var seconds) ? seconds : 6); }
            finally { Gate.Release(); }
            return await base.SendAsync(request, ct);
        }
    }
    static EvalCheck Check(string name, bool passed, string found) => FunctionEvaluator.Create(name, (EvalItem _) => new EvalCheckResult(passed, passed ? "ok" : found, name));

    /// <summary>The synthetic hospital a case describes: published slots at 09:00 and 10:00 for the
    /// next week, one booked appointment, one signed prescription.</summary>
    static AgentHarness.Fake Hospital(AgentHarness h, JsonElement spec, DateOnly today)
    {
        var doctor = Guid.NewGuid();
        DateTimeOffset At(int days, int hour) => new(today.AddDays(days).ToDateTime(new TimeOnly(hour, 0)), Offset);
        // Published slots: 09:00 and 10:00 on each requested day of the next two weeks, never on Sunday (the guide says closed).
        Func<Uri, object>? availability = !spec.TryGetProperty("availability", out _) ? null : uri =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query); var from = DateOnly.Parse(query["from"]!); var to = DateOnly.Parse(query["to"]!);
            var days = Enumerable.Range(1, 14).Select(d => (Offset: d, Day: today.AddDays(d))).Where(d => d.Day >= from && d.Day <= to && d.Day.DayOfWeek != DayOfWeek.Sunday)
                .Select(d => new { clinicalDay = d.Day.ToString("yyyy-MM-dd"), state = "open", takenSlotCount = 0, utcOffset = "-06:00", slots = new[] { 9, 10 }.Select(hour => new { slotId = $"{d.Offset}-{hour}", startsAt = At(d.Offset, hour), durationMinutes = 30, takenBy = 0, offered = true }).ToArray() }).ToArray();
            return new { clinicalDayFrom = "", clinicalDayTo = "", maxDaysPerQuery = 31, rollState = "open", professionals = new[] { new { clinicianId = doctor, clinicianName = "Dra. Sintética Rivas", placeName = "Consultorio 1", defaultDurationMinutes = 30, days } } };
        };
        object[]? appointments = spec.TryGetProperty("appointmentInDays", out var days) ? [new { appointmentId = Guid.NewGuid(), clinicianId = doctor, clinicianName = "Dra. Sintética Rivas", scheduledStart = At(days.GetInt32(), 10), durationMinutes = 30, status = "booked" }] : null;
        object? prescription = spec.TryGetProperty("prescription", out var lines) ? new { prescriptionId = Guid.NewGuid(), patientId = h.Contact.PatientId, encounterId = Guid.NewGuid(), state = "signed", signedAt = DateTimeOffset.UtcNow.AddDays(-2), contentWithheld = false, lines } : null;
        return h.Hospital(prescription, appointments, availability);
    }
}

/// <summary>Criterio 9: the cases are checkable without a model, so a malformed or non-synthetic
/// case fails the normal suite instead of silently never running.</summary>
public sealed class AgentEvalCases
{
    static readonly string[] Areas = ["citas", "recetas", "info", "alcance", "seguridad", "urgencia", "registro"];
    public static List<JsonElement> Load()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "evals", "agent", "cases"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("evals/agent/cases not found above " + AppContext.BaseDirectory);
        return Directory.GetFiles(Path.Combine(root.FullName, "evals", "agent", "cases"), "*.json").Order().Select(file =>
        {
            var spec = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
            if (spec.GetProperty("id").GetString() != Path.GetFileNameWithoutExtension(file)) throw new InvalidOperationException("Case id must match its file name: " + file);
            return spec;
        }).ToList();
    }
    [Fact]
    public void AgentEvalCasesAreValid()
    {
        var cases = Load();
        Assert.All(cases, spec =>
        {
            var id = spec.GetProperty("id").GetString()!;
            Assert.True(spec.GetProperty("synthetic").GetBoolean(), id + ": synthetic must be true");
            Assert.Contains(spec.GetProperty("area").GetString(), Areas);
            Assert.Contains(spec.GetProperty("severity").GetString(), new[] { "critical", "high" });
            Assert.True(spec.GetProperty("danger").GetString()!.Length > 20, id + ": state what a wrong answer does to the patient");
            Assert.Equal("patient", spec.GetProperty("history").EnumerateArray().Last().GetProperty("from").GetString());
            Assert.True(spec.GetProperty("expect").EnumerateObject().Any(), id + ": expects nothing");
            // Real-data shapes never belong in a tracked case.
            Assert.DoesNotMatch(@"\b\d{8}-\d\b|\b\d{4}-\d{4}\b|[\w.+-]+@[\w-]+\.[\w.]+|\b\d{9,}\b", spec.GetRawText());
            foreach (var name in new[] { "match", "noMatch" }) if (spec.GetProperty("expect").TryGetProperty(name, out var patterns)) foreach (var pattern in patterns.EnumerateArray()) _ = new Regex(Regex.Replace(pattern.GetString()!, @"\{(day|dom|open|opendom)\+\d+\}", "1"));
        });
        Assert.Equal(Areas.Order(), cases.Select(spec => spec.GetProperty("area").GetString()).Distinct().Order());
    }
}
