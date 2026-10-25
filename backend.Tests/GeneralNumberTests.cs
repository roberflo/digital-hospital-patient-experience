using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Recepcion;
using Recepcion.Integrations;
using Xunit;

/// <summary>recordatorios-whatsapp.spec.md S3-R (criterios 17–27, 39–40): hospitals without Recepción get their reminders from the platform's
/// general number. Hospital is a double with exactly the shape of the plan's contract (criterio 41 and L1 with outboundDisplayName); Kapso is a
/// double. Every phone is random per test: the opt-out list and the reply cap are global by design (A17).</summary>
// Serialized with the other classes that count tenants and audits across the database.
[Collection("Onboarding database")]
public sealed class GeneralNumberTests : IAsyncLifetime
{
    const string HashKey = "synthetic-phone-hash-key-0123456789", ClinicSecret = "synthetic-clinic-webhook-secret", PlatformSecret = "synthetic-platform-webhook-secret";
    const string PlatformNumber = "platform-number-synthetic", PlatformKey = "platform-key-synthetic", ClinicKey = "synthetic";
    const string FreeText = "Este número sólo envía recordatorios. Para hablar con su clínica, llame al número que aparece en su recordatorio. Para no recibir más recordatorios escriba BAJA.";
    readonly AgentHarness own = new(phone: RandomPhone());
    readonly string phone = RandomPhone();
    readonly string sub = Guid.NewGuid().ToString();
    Guid clinicA, clinicB;
    readonly Guid appointmentA = Guid.NewGuid(), appointmentB = Guid.NewGuid(), patientA = Guid.NewGuid(), patientB = Guid.NewGuid();
    // The day after tomorrow at 10:00 in El Salvador, as the database keeps it.
    readonly DateTimeOffset start = ReminderRules.Instant(new DateTimeOffset(DateTime.UtcNow.Date.AddDays(2).AddHours(16), TimeSpan.Zero));
    // Hospital double, per tenant (each tenant has its own host).
    readonly Dictionary<Guid, List<object>> recipients = [];
    readonly Dictionary<Guid, object> contacts = [];
    readonly List<string> hospitalReads = [];
    readonly List<(Guid Tenant, string Path, string Body)> hospitalWrites = [];
    bool hospitalDown, hospitalThrowsOnce; HttpStatusCode cancelAnswer = HttpStatusCode.NoContent, withdrawAnswer = HttpStatusCode.OK;
    // Kapso double.
    readonly List<(string Key, string Path, JsonElement Body)> kapso = [];
    readonly Dictionary<string, string?> waIds = []; // E.164 digits ⇒ the wa_id Kapso answers with
    int kapsoCalls;

    static string RandomPhone() => "5037" + Random.Shared.Next(1000000, 9999999).ToString(CultureInfo.InvariantCulture);
    string Hash(string value) => Rules.PhoneHash(value, HashKey);

    public async Task InitializeAsync()
    {
        await own.Start();
        clinicA = await Clinic("Clínica Sintética A · nombre interno");
        clinicB = await Clinic("Clínica Sintética B · nombre interno");
        contacts[clinicA] = new { publicAddress = "Calle Sintética 1", publicPhone = "+503 2200 0001", outboundDisplayName = "Clínica Pública A" };
        contacts[clinicB] = new { publicAddress = "Calle Sintética 2", publicPhone = "+503 2200 0002", outboundDisplayName = "Clínica Pública B" };
    }
    public Task DisposeAsync() => own.DisposeAsync().AsTask();

    async Task<Guid> Clinic(string name)
    {
        var id = Guid.NewGuid();
        await using var db = Db(id);
        db.Tenants.Add(new Tenant { Id = id, Name = name, Mode = "reminders", RemindersEnabled = true, AgentEnabled = false });
        await db.SaveChangesAsync(); return id;
    }
    CrmDb Db(Guid tenant) => own.NewDb(new TenantScope { Id = tenant });
    object Recipient(Guid appointment, Guid patient, DateTimeOffset at, string? to = null, DateTimeOffset? consentSince = null) => new { appointmentId = appointment, patientId = patient, scheduledStart = at, phone = to ?? phone, consentSince = consentSince ?? DateTimeOffset.UtcNow.AddDays(-30) };

    IConfiguration Config(Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["PHONE_HASH_KEY"] = HashKey, ["REMINDERS_SEND_ENABLED"] = "true", ["SEND_ENABLED"] = "true", ["KAPSO_API_KEY"] = ClinicKey, ["KAPSO_WEBHOOK_SECRET"] = ClinicSecret,
            ["KAPSO_PLATFORM_API_KEY"] = PlatformKey, ["KAPSO_PLATFORM_WEBHOOK_SECRET"] = PlatformSecret, ["KAPSO_PLATFORM_PHONE_NUMBER_ID"] = PlatformNumber,
        };
        foreach (var tenant in new[] { clinicA, clinicB, own.Scope.Id })
        {
            var token = "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { tenant_id = tenant, exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic";
            values[$"Hospital:Tenants:{tenant}:BaseUrl"] = $"https://{tenant:N}.hospital.example.invalid/"; values[$"Hospital:Tenants:{tenant}:AccessToken"] = token;
        }
        foreach (var (k, v) in extra ?? []) values[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
    /// <summary>Hospital with only the routes S3-H publishes (spec criterios 20, 33, 41, and the existing cancel), each in its documented shape.
    /// Any other route is 404, as Hospital answers it: a caller on the wrong route fails here instead of passing against a lenient double.</summary>
    AgentHarness.Fake Hospital() => new(async request =>
    {
        static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        var tenant = Guid.ParseExact(request.RequestUri!.Host.Split('.')[0], "N"); var path = request.RequestUri.AbsolutePath;
        if (hospitalDown) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        if (request.Method == HttpMethod.Get)
        {
            hospitalReads.Add(path);
            if (path == "/v1/agenda/whatsapp-reminders")
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                if (!DateOnly.TryParseExact(query["from"], "yyyy-MM-dd", out var from) || !DateOnly.TryParseExact(query["to"], "yyyy-MM-dd", out var to) || to < from || to.DayNumber - from.DayNumber > 6) return Status(HttpStatusCode.BadRequest);
                return AgentHarness.Json(new { rows = recipients.GetValueOrDefault(tenant) ?? [] });
            }
            if (path == "/v1/agenda/clinic-contact" && contacts.TryGetValue(tenant, out var contact)) return AgentHarness.Json(contact);
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var withdraw = segments is ["v1", "patients", var p, "whatsapp-reminders", "consent", "withdraw"] && Guid.TryParse(p, out _);
        var cancel = segments is ["v1", "agenda", var a, "cancel"] && Guid.TryParse(a, out _);
        if (request.Method != HttpMethod.Post || !withdraw && !cancel) return Status(HttpStatusCode.NotFound);
        if (hospitalThrowsOnce) { hospitalThrowsOnce = false; throw new InvalidOperationException("synthetic failure inside the webhook"); }
        hospitalWrites.Add((tenant, path, await request.Content!.ReadAsStringAsync()));
        // Criterio 33 (N4): an authorisation granted after optedOutAt is not withdrawn; 200 with the current state either way.
        return cancel ? Status(cancelAnswer) : withdrawAnswer == HttpStatusCode.OK ? AgentHarness.Json(new { active = false, since = (DateTimeOffset?)null }) : Status(withdrawAnswer);
    });
    static object Template(string name, string body, params string[] buttons) => new
    {
        name, language = "es", status = "APPROVED", category = "UTILITY", parameter_format = "NAMED",
        components = buttons.Length == 0 ? new object[] { new { type = "BODY", text = body } }
            : new object[] { new { type = "BODY", text = body }, new { type = "BUTTONS", buttons = buttons.Select(text => new { type = "QUICK_REPLY", text }) } },
    };
    AgentHarness.Fake Kapso() => new(async request =>
    {
        kapsoCalls++;
        var key = request.Headers.GetValues("X-API-Key").Single(); var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get)
            return path.Contains("phone_numbers") ? AgentHarness.Json(new { data = new { business_account_id = "synthetic-waba" } }) : AgentHarness.Json(new { data = new[] {
                Template(KapsoClient.GeneralDayTemplate, "Cita en {{hospital}} el {{fecha}} a las {{hora}}. Llame al {{telefono}}. BAJA", "Cancelar cita"),
                Template(KapsoClient.GeneralHourTemplate, "Cita hoy en {{hospital}} el {{fecha}} a las {{hora}}. Llame al {{telefono}}. BAJA"),
                Template(ReminderRules.DayTemplateV4, "Cita en {{hospital}} el {{fecha}} a las {{hora}}. BAJA", "Confirmar", "Cambiar fecha", "Cancelar"),
                Template("recepcion_cita_una_hora_v1", "Cita en {{hospital}} el {{fecha}} a las {{hora}}. BAJA") } });
        using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        kapso.Add((key, path, sent.RootElement.Clone()));
        // The measured shape (criterio 21): the wa_id is the «to» without «+», unless the test says otherwise (null: none came back).
        var to = sent.RootElement.GetProperty("to").GetString()!.TrimStart('+');
        return AgentHarness.KapsoSent("out-" + Guid.NewGuid(), waIds.TryGetValue(to, out var waId) ? waId : to);
    });
    AppointmentReminderService Service(Guid tenant, CrmDb db, HttpMessageHandler kapsoDouble, IConfiguration? config = null)
    {
        config ??= Config(); var scope = new TenantScope { Id = tenant }; var client = new KapsoClient(new HttpClient(kapsoDouble), config);
        var reminders = new AppointmentReminderService(db, scope, new HospitalClient(new HttpClient(Hospital()), config), client, new ConversationService(db, scope, client), config);
        return reminders;
    }
    /// <summary>One worker pass of sync for a clinic, on its own context.</summary>
    async Task Sync(Guid tenant, DateTimeOffset? now = null)
    {
        await using var db = Db(tenant); await Service(tenant, db, Kapso()).Sync(now ?? DateTimeOffset.UtcNow);
    }
    async Task Dispatch(Guid tenant, Guid id, DateTimeOffset now, IConfiguration? config = null, HttpMessageHandler? kapsoDouble = null)
    {
        await using var db = Db(tenant); await Service(tenant, db, kapsoDouble ?? Kapso(), config).Dispatch(id, now);
    }
    async Task<List<AppointmentReminder>> Rows(Guid tenant) { await using var db = Db(tenant); return await db.AppointmentReminders.AsNoTracking().OrderBy(r => r.Window).ToListAsync(); }
    async Task<AppointmentReminder> Row(Guid tenant, Guid id) { await using var db = Db(tenant); return await db.AppointmentReminders.AsNoTracking().SingleAsync(r => r.Id == id); }
    /// <summary>A day-before reminder the general number already sent to <see cref="phone"/>.</summary>
    async Task<AppointmentReminder> Sent(Guid tenant, Guid appointment, Guid patient, string status = "sent", DateTimeOffset? at = null, string? to = null, string window = "day_before")
    {
        var startsAt = at ?? start;
        var row = new AppointmentReminder { TenantId = tenant, PatientId = patient, AppointmentId = appointment, StartsAt = startsAt, DueAt = window == "day_before" ? startsAt.AddDays(-1) : startsAt.AddHours(-1), Window = window, Status = status, RecipientHash = Hash(to ?? phone), AttemptedAt = status == "pending" ? null : DateTimeOffset.UtcNow };
        await using var db = Db(tenant); db.Add(row); await db.SaveChangesAsync(); return row;
    }

    /// <summary>An inbound message to the general number, signed as Kapso signs it. <paramref name="payload"/>: a template quick reply (CANCELAR …)
    /// or an interactive reply (CANCELAR-SI/NO …); otherwise <paramref name="text"/>.</summary>
    async Task<IResult> Inbound(string from, string? payload = null, string? text = null, string secret = PlatformSecret, string number = PlatformNumber, IConfiguration? config = null, string? messageId = null)
    {
        var mid = messageId ?? "wamid." + Guid.NewGuid().ToString("N");
        object message = payload is null ? new { id = mid, from, type = "text", text = new { body = text }, kapso = new { direction = "inbound", status = "received", origin = "cloud_api", content = text } }
            : payload.StartsWith("CANCELAR-", StringComparison.Ordinal)
            ? new { id = mid, from, type = "interactive", interactive = new { type = "button_reply", button_reply = new { id = payload, title = payload.StartsWith("CANCELAR-SI", StringComparison.Ordinal) ? "Sí, cancelar" : "No" } }, kapso = new { direction = "inbound", status = "received", origin = "cloud_api" } }
            : new { id = mid, from, type = "button", button = new { payload, text = "Cancelar cita" }, kapso = new { direction = "inbound", status = "received", origin = "cloud_api", content = "Cancelar cita" } };
        var body = JsonSerializer.SerializeToUtf8Bytes(new { phone_number_id = number, conversation = new { id = "conv-synthetic", phone_number = from }, message });
        var ctx = new DefaultHttpContext(); ctx.Request.Body = new MemoryStream(body); ctx.Request.ContentLength = body.Length;
        ctx.Request.Headers["X-Webhook-Signature"] = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();
        ctx.Request.Headers["X-Webhook-Event"] = "whatsapp.message.received"; ctx.Request.Headers["X-Idempotency-Key"] = Guid.NewGuid().ToString();
        config ??= Config(); var scope = new TenantScope();
        await using var db = own.NewDb(scope);
        return await GeneralNumber.Receive(ctx, db, scope, config, new KapsoClient(new HttpClient(Kapso()), config), new HospitalClient(new HttpClient(Hospital()), config), NullLoggerFactory.Instance);
    }
    /// <summary>What the general number answered, without the recipient: the bytes a patient sees.</summary>
    static string Shown(JsonElement sent) { var node = JsonNode.Parse(sent.GetRawText())!.AsObject(); node.Remove("to"); return node.ToJsonString(); }
    /// <summary>What a message to the general number could have created: a contact for that phone in any tenant (other test classes write in
    /// parallel, so «any tenant» is read by the phone's hash), and conversations, messages or agent jobs in the tenants this test owns.</summary>
    async Task<(int Contacts, int Conversations, int Messages, int Jobs)> Everywhere()
    {
        await using var db = own.NewDb(new TenantScope()); var mine = new[] { clinicA, clinicB, own.Scope.Id }; var hash = Hash(phone);
        return (await db.Contacts.IgnoreQueryFilters().CountAsync(c => c.PhoneHash == hash), await db.Conversations.IgnoreQueryFilters().CountAsync(c => mine.Contains(c.TenantId)),
            await db.Messages.IgnoreQueryFilters().CountAsync(m => mine.Contains(m.TenantId)), await db.Jobs.IgnoreQueryFilters().CountAsync(j => mine.Contains(j.TenantId)));
    }

    // ---------------- Criterio 17: provisioning ----------------
    static IConfiguration Install() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HOSPITAL_API_URL"] = "https://hospital-api.example.com", ["HOSPITAL_ALLOWED_API_ORIGINS"] = "https://hospital-api.example.com" }).Build();
    DefaultHttpContext Principal(string roles, Guid? tenant = null, bool owner = true)
    {
        var claims = new List<Claim> { new("sub", sub), new("name", "Operadora Sintética"), new("realm_access", "{\"roles\":[" + roles + "]}") };
        if (tenant is { } id) claims.Add(new("tenant_id", id.ToString()));
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        ctx.Request.Headers.Authorization = "Bearer owner.token.synthetic"; return ctx;
    }
    sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }
    static AgentHarness.Fake Listing(params Guid[] hospitals) => Listing("America/El_Salvador", hospitals);
    static AgentHarness.Fake Listing(string zone, params Guid[] hospitals) => new(request =>
    {
        Assert.Equal("/v1/platform/hospitals", request.RequestUri!.AbsolutePath);
        return Task.FromResult(AgentHarness.Json(new { hospitals = hospitals.Select(id => new { id, displayName = "Hospital Sintético Listado", timeZone = zone }) }));
    });
    async Task<IResult> Provision(Guid tenant, DefaultHttpContext who, bool enabled = true, params Guid[] listed) => await Provision(tenant, who, Listing(listed), enabled);
    async Task<IResult> Provision(Guid tenant, DefaultHttpContext who, AgentHarness.Fake listing, bool enabled = true)
    {
        var scope = new TenantScope(); await using var db = own.NewDb(scope); // one scope per request, as DI gives it
        return await PlatformEndpoints.Put(tenant, new ReminderClinicInput(enabled), who, db, scope, Install(), new Factory(listing), default);
    }
    static int? Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;

    [Theory]
    [InlineData("\"Administrador\"", false)] // a hospital's own Administrador
    [InlineData("\"platform-owner\"", true)] // the platform role on a hospital token (with tenant_id)
    [InlineData("", false)]
    public async Task OnlyThePlatformOperatorProvisions(string roles, bool withTenant)
    {
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<AccessDeniedException>(() => Provision(id, Principal(roles, withTenant ? id : null), listed: id));
        await using var db = own.NewDb(new TenantScope());
        Assert.False(await db.Tenants.AnyAsync(t => t.Id == id));
    }
    [Fact]
    public async Task ProvisioningCreatesARemindersClinicWithoutMembersOrAgentAndIsAudited()
    {
        var id = Guid.NewGuid();
        await using var before = own.NewDb(new TenantScope()); var ownBefore = await before.Tenants.AsNoTracking().SingleAsync(t => t.Id == own.Scope.Id);

        Assert.Equal(200, Status(await Provision(id, Principal("\"platform-owner\""), listed: id)));

        await using var db = own.NewDb(new TenantScope());
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == id);
        Assert.Equal(("reminders", false, true), (tenant.Mode, tenant.AgentEnabled, tenant.RemindersEnabled));
        Assert.Equal(0, await db.Members.IgnoreQueryFilters().CountAsync(m => m.TenantId == id));
        var audit = Assert.Single(await db.Audits.IgnoreQueryFilters().Where(a => a.TenantId == id).ToListAsync());
        Assert.Equal(("platform:" + sub, "reminder_clinic.provisioned", id.ToString()), (audit.Actor, audit.Action, audit.Resource));
        var ownAfter = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == own.Scope.Id);
        Assert.Equal((ownBefore.Mode, ownBefore.Name, ownBefore.RemindersEnabled, ownBefore.AgentEnabled), (ownAfter.Mode, ownAfter.Name, ownAfter.RemindersEnabled, ownAfter.AgentEnabled));
    }
    [Fact]
    public async Task AHospitalHospitalDoesNotListIsNotCreated()
    {
        var id = Guid.NewGuid();
        Assert.Equal(404, Status(await Provision(id, Principal("\"platform-owner\""), listed: Guid.NewGuid())));
        await using var db = own.NewDb(new TenantScope());
        Assert.False(await db.Tenants.AnyAsync(t => t.Id == id));
    }
    [Fact]
    public async Task AHospitalWithRecepcionIsNeverTurnedIntoAGeneralNumberClinic()
    {
        Assert.Equal(409, Status(await Provision(own.Scope.Id, Principal("\"platform-owner\""), listed: own.Scope.Id)));
        await using var db = own.NewDb(new TenantScope());
        Assert.Equal("reception", (await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == own.Scope.Id)).Mode);
    }
    [Fact]
    public async Task NobodyOfARemindersClinicSignsIntoRecepcion()
    {
        var ctx = Principal("\"Administrador\"", clinicA); ctx.User.Identities.Single().AddClaim(new Claim("iss", "https://identity.example.invalid/realms/hospital"));
        await using var db = Db(clinicA);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Authority"] = "https://identity.example.invalid/realms/hospital" }).Build();
        Assert.False(await Identity.Bind(ctx, db, new TenantScope(), new CurrentUser(), config));
        Assert.True(ctx.Items.ContainsKey("hospital-reminders-only")); // Program.cs answers it with a 403 that says why
        Assert.Equal(0, await db.Members.IgnoreQueryFilters().CountAsync(m => m.TenantId == clinicA));
    }
    // A16: a clinic that contracts Recepción leaves the general number; its rows there never attempted are deleted (so S1 can create its own,
    // one per appointment, start and window), the sent ones stay for a later BAJA.
    [Fact]
    public async Task LeavingTheGeneralNumberDeletesWhatItNeverAttempted()
    {
        var pending = await Sent(clinicA, appointmentA, patientA, "pending");
        var skipped = await Sent(clinicA, appointmentA, patientA, "pending", window: "hour_before");
        await using (var db = Db(clinicA)) await db.AppointmentReminders.Where(r => r.Id == skipped.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.Status, "skipped"));
        var sent = await Sent(clinicA, appointmentB, patientA);
        var scope = new TenantScope();
        await using (var db = own.NewDb(scope))
            Assert.Equal(204, Status(await PlatformEndpoints.Delete(clinicA, Principal("\"platform-owner\""), db, scope, default)));
        await using var read = Db(clinicA);
        var tenant = await read.Tenants.AsNoTracking().SingleAsync(t => t.Id == clinicA);
        Assert.Equal(("reception", false), (tenant.Mode, tenant.RemindersEnabled));
        Assert.Equal([sent.Id], await read.AppointmentReminders.AsNoTracking().Select(r => r.Id).ToListAsync());
        Assert.Contains(await read.Audits.AsNoTracking().ToListAsync(), a => a.Action == "reminder_clinic.removed" && a.Actor == "platform:" + sub);
    }

    // ---------------- Criterio 19: recipients only from the minimal contract ----------------
    [Fact]
    public async Task SyncCreatesRowsOnlyFromTheMinimalContract()
    {
        await Sync(clinicA);
        Assert.Empty(await Rows(clinicA)); // absent from criterio 41 ⇒ no row

        recipients[clinicA] = [Recipient(appointmentA, patientA, start)];
        await Sync(clinicA, DateTimeOffset.UtcNow.AddMinutes(16));

        var rows = await Rows(clinicA);
        Assert.Equal(["day_before", "hour_before"], rows.Select(r => r.Window));
        Assert.All(rows, r => Assert.Equal((null, null, patientA, appointmentA, start, Hash(phone), "pending"), ((Guid?)r.ContactId, (Guid?)r.ChannelId, r.PatientId, r.AppointmentId, r.StartsAt, r.RecipientHash, r.Status)));
        Assert.NotEmpty(hospitalReads);
        Assert.All(hospitalReads, path => Assert.Equal("/v1/agenda/whatsapp-reminders", path)); // never agenda/day, agenda/range or patients/{id} (B7)
    }
    [Fact]
    public async Task ADispatchRereadsTheContractAndCancelsWhatChanged()
    {
        recipients[clinicA] = [Recipient(appointmentA, patientA, start)];
        await Sync(clinicA);
        var day = (await Rows(clinicA)).Single(r => r.Window == "day_before");

        recipients[clinicA] = []; // cancelled, moved or authorisation withdrawn in Hospital
        await Dispatch(clinicA, day.Id, day.DueAt);
        Assert.Equal("cancelled", (await Row(clinicA, day.Id)).Status);

        var hour = (await Rows(clinicA)).Single(r => r.Window == "hour_before");
        recipients[clinicA] = [Recipient(appointmentA, patientA, start, RandomPhone())]; // the patient's phone changed
        await Dispatch(clinicA, hour.Id, hour.DueAt);
        Assert.Equal("cancelled", (await Row(clinicA, hour.Id)).Status);
        Assert.Empty(kapso);
        Assert.All(hospitalReads, path => Assert.True(path is "/v1/agenda/whatsapp-reminders" or "/v1/agenda/clinic-contact", path));
    }

    // ---------------- Criterios 18, 20, 21, 39: what goes out and through which account ----------------
    [Fact]
    public async Task TheGeneralReminderGoesFromThePlatformNumberWithTheApprovedNameAndOneOpaqueButton()
    {
        recipients[clinicA] = [Recipient(appointmentA, patientA, start)];
        await Sync(clinicA);
        var day = (await Rows(clinicA)).Single(r => r.Window == "day_before");

        await Dispatch(clinicA, day.Id, day.DueAt);

        Assert.Equal("sent", (await Row(clinicA, day.Id)).Status);
        var (key, path, body) = Assert.Single(kapso);
        Assert.Equal(PlatformKey, key); // criterio 39
        Assert.Equal($"/meta/whatsapp/v24.0/{PlatformNumber}/messages", path); // criterio 18
        var template = body.GetProperty("template");
        Assert.Equal(KapsoClient.GeneralDayTemplate, template.GetProperty("name").GetString());
        var values = template.GetProperty("components")[0].GetProperty("parameters").EnumerateArray().ToDictionary(p => p.GetProperty("parameter_name").GetString()!, p => p.GetProperty("text").GetString());
        Assert.Equal("Clínica Pública A", values["hospital"]); // criterio 20: outboundDisplayName, never Tenant.Name
        Assert.Equal("+503 2200 0001", values["telefono"]);
        var button = Assert.Single(template.GetProperty("components").EnumerateArray(), c => c.GetProperty("type").GetString() == "button");
        Assert.Equal($"CANCELAR {day.Id}", button.GetProperty("parameters")[0].GetProperty("payload").GetString()); // criterio 21
        var raw = body.GetRawText();
        foreach (var leak in new[] { appointmentA.ToString(), clinicA.ToString(), patientA.ToString(), "nombre interno" }) Assert.DoesNotContain(leak, raw);
        await using var db = own.NewDb(new TenantScope());
        Assert.Equal(0, await db.Conversations.IgnoreQueryFilters().CountAsync(c => c.TenantId == clinicA)); // no Conversation, no Message (A17)
        Assert.Equal(0, await db.Messages.IgnoreQueryFilters().CountAsync(m => m.TenantId == clinicA));
    }
    [Fact]
    public async Task WithoutAnApprovedPublicNameNothingGoesOut()
    {
        contacts.Remove(clinicA); // L1 answers 404
        recipients[clinicA] = [Recipient(appointmentA, patientA, start)];
        await Sync(clinicA);
        var day = (await Rows(clinicA)).Single(r => r.Window == "day_before");

        await Dispatch(clinicA, day.Id, day.DueAt);

        var row = await Row(clinicA, day.Id);
        Assert.Equal(("pending", "Falta el nombre público de la clínica."), (row.Status, row.Reason));
        Assert.Empty(kapso);
    }
    [Fact]
    public async Task WithoutPlatformCredentialsNothingIsCalledAndTheQueueSaysWhy() // criterio 40
    {
        recipients[clinicA] = [Recipient(appointmentA, patientA, start)];
        await Sync(clinicA);
        var day = (await Rows(clinicA)).Single(r => r.Window == "day_before");
        var reads = hospitalReads.Count; var kapsoDouble = Kapso();

        await Dispatch(clinicA, day.Id, day.DueAt, Config(new() { ["KAPSO_PLATFORM_API_KEY"] = "", ["KAPSO_PLATFORM_PHONE_NUMBER_ID"] = "" }), kapsoDouble);

        var row = await Row(clinicA, day.Id);
        Assert.Equal(("pending", "Cuenta general no configurada"), (row.Status, row.Reason));
        Assert.Equal(0, kapsoDouble.Calls); Assert.Equal(reads, hospitalReads.Count);
    }
    // Criterios 18 y 39: a clinic with Recepción always sends from its own number with its own key, and never falls to the general one.
    [Theory][InlineData(true)][InlineData(false)]
    public async Task AClinicWithRecepcionNeverUsesTheGeneralNumber(bool ownNumberEnabled)
    {
        await using (var db = own.NewContext())
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == own.Scope.Id); tenant.RemindersEnabled = true; tenant.ReminderChannelId = own.Conversation.ChannelId;
            var channel = await db.Channels.SingleAsync(c => c.Id == own.Conversation.ChannelId); channel.Enabled = ownNumberEnabled;
            var contact = await db.Contacts.SingleAsync(c => c.Id == own.Contact.Id); contact.PatientId = patientA; contact.ReminderConsentAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        own.Contact.PatientId = patientA;
        var row = new AppointmentReminder { TenantId = own.Scope.Id, ContactId = own.Contact.Id, ChannelId = own.Conversation.ChannelId, PatientId = patientA, AppointmentId = appointmentA, StartsAt = start, DueAt = start.AddDays(-1), Window = "day_before" };
        await using (var db = own.NewContext()) { db.Add(row); await db.SaveChangesAsync(); }
        var booked = new { appointmentId = appointmentA, clinicianId = Guid.NewGuid(), clinicianName = "Dra. Sintética", placeName = "Consultorio", scheduledStart = start, durationMinutes = 30, status = "booked" };

        await own.Reminders(Kapso(), own.Hospital(appointments: [booked]), new() { ["REMINDERS_SEND_ENABLED"] = "true", ["KAPSO_PLATFORM_API_KEY"] = PlatformKey, ["KAPSO_PLATFORM_PHONE_NUMBER_ID"] = PlatformNumber }).Dispatch(row.Id, row.DueAt);

        if (!ownNumberEnabled) { Assert.Empty(kapso); return; }
        var (key, path, _) = Assert.Single(kapso);
        Assert.Equal(ClinicKey, key); Assert.DoesNotContain(PlatformNumber, path);
    }
    [Fact]
    public async Task TheGeneralWebhookOnlyAcceptsThePlatformSecret() // criterio 39
    {
        var result = await Inbound(phone, text: "hola", secret: ClinicSecret);
        Assert.Equal(401, Status(result));
        Assert.Empty(kapso);
    }
    [Fact]
    public void TheGeneralTemplateInTheRepoIsValidOnlyForTheGeneralWindow()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "integrations", "kapso", "templates"))) root = root.Parent;
        var template = JsonNode.Parse(File.ReadAllText(Path.Combine(root!.FullName, "integrations", "kapso", "templates", "general_dia_anterior.json")))!.AsObject();
        Assert.Equal(KapsoClient.GeneralDayTemplate, (string?)template["name"]);
        template["status"] = "APPROVED"; var json = JsonSerializer.SerializeToElement(template);
        Assert.True(KapsoClient.ValidReminderTemplate(json, KapsoClient.GeneralDayWindow));
        Assert.False(KapsoClient.ValidReminderTemplate(json, "day_before"));
    }

    // ---------------- Criterios 22, 23, 27: cancel with two taps, routed only by the reminder ----------------
    [Fact]
    public async Task CancellingTakesTwoTapsAndReachesHospitalOnceAsThePatient()
    {
        recipients[clinicA] = [Recipient(appointmentA, patientA, start)];
        var row = await Sent(clinicA, appointmentA, patientA);

        await Inbound(phone, $"CANCELAR {row.Id}");
        Assert.Empty(hospitalWrites);
        var (_, _, ask) = Assert.Single(kapso);
        Assert.Equal("interactive", ask.GetProperty("type").GetString());
        Assert.Equal([($"CANCELAR-SI {row.Id}", "Sí, cancelar"), ($"CANCELAR-NO {row.Id}", "No")], AgentHarness.Options(ask.GetProperty("interactive")));

        await Inbound(phone, $"CANCELAR-NO {row.Id}");
        Assert.Empty(hospitalWrites);
        Assert.Equal("sent", (await Row(clinicA, row.Id)).Status);

        await Inbound(phone, $"CANCELAR-SI {row.Id}");
        await Inbound(phone, $"CANCELAR-SI {row.Id}"); // a double tap, or Kapso redelivering

        var write = Assert.Single(hospitalWrites);
        Assert.Equal((clinicA, $"/v1/agenda/{appointmentA}/cancel"), (write.Tenant, write.Path));
        using var cancel = JsonDocument.Parse(write.Body); // criterio 27
        Assert.Equal(("patient-requested", true), (cancel.RootElement.GetProperty("reason").GetString(), cancel.RootElement.GetProperty("cancelledByPatient").GetBoolean()));
        Assert.Equal("cancelled", (await Row(clinicA, row.Id)).Status);
        await using var db = own.NewDb(new TenantScope()); // criterio 26: the cancellation leaves one Activity, only in that clinic
        var activities = await db.Activities.IgnoreQueryFilters().Where(a => a.Kind == "appointment_cancelled_general" && (a.TenantId == clinicA || a.TenantId == clinicB)).ToListAsync();
        Assert.Equal(clinicA, Assert.Single(activities).TenantId);
    }
    public enum Wrong { OtherPhone, AlreadyCancelled, Moved, Past, OwnNumberRow, WithdrawnInHospital }
    [Theory]
    [InlineData(Wrong.OtherPhone)][InlineData(Wrong.AlreadyCancelled)][InlineData(Wrong.Moved)][InlineData(Wrong.Past)][InlineData(Wrong.OwnNumberRow)][InlineData(Wrong.WithdrawnInHospital)]
    public async Task AWrongTapChangesNothingAndReadsLikeFreeText(Wrong wrong)
    {
        // The same phone has an appointment in both clinics (INV-7, INV-8).
        recipients[clinicA] = [Recipient(appointmentA, patientA, start)]; recipients[clinicB] = [Recipient(appointmentB, patientB, start)];
        var rowA = await Sent(clinicA, appointmentA, patientA, wrong == Wrong.AlreadyCancelled ? "cancelled" : "sent", wrong == Wrong.Past ? DateTimeOffset.UtcNow.AddMinutes(-5) : null);
        await Sent(clinicB, appointmentB, patientB);
        if (wrong == Wrong.Moved) recipients[clinicA] = [Recipient(appointmentA, patientA, start.AddHours(2))];
        if (wrong == Wrong.WithdrawnInHospital) recipients[clinicA] = [];
        var id = rowA.Id;
        if (wrong == Wrong.OwnNumberRow)
        {
            var ownRow = new AppointmentReminder { TenantId = own.Scope.Id, ContactId = own.Contact.Id, ChannelId = own.Conversation.ChannelId, PatientId = patientA, AppointmentId = appointmentA, StartsAt = start, DueAt = start.AddDays(-1), Window = "day_before", Status = "sent", AttemptedAt = DateTimeOffset.UtcNow };
            await using var db = own.NewContext(); db.Add(ownRow); await db.SaveChangesAsync(); id = ownRow.Id;
        }
        var sender = wrong == Wrong.OtherPhone ? RandomPhone() : phone;

        await Inbound(sender, $"CANCELAR {id}");
        await Inbound(sender, $"CANCELAR-NO {id}"); // «No» does not re-read Hospital: only the row's own checks stand between it and an answer
        await Inbound(sender, $"CANCELAR-SI {id}");
        await Inbound(RandomPhone(), text: "hola"); // free text from a phone with no history

        Assert.Empty(hospitalWrites);
        Assert.Equal(wrong == Wrong.AlreadyCancelled ? "cancelled" : "sent", (await Row(clinicA, rowA.Id)).Status);
        var replies = kapso.Select(k => k.Body).ToList();
        Assert.Equal(2, replies.Count); // the later wrong taps fall under the 24 h cap, exactly like a second free text
        Assert.Equal(Shown(replies[1]), Shown(replies[0])); // byte for byte, without the recipient
        Assert.Equal(FreeText, replies[0].GetProperty("text").GetProperty("body").GetString());
    }

    // Criterio 30: a wa_id of 8 digits in all is unusable on the general number too: no reply (it would go to +503…), nothing stored.
    [Fact]
    public async Task AnEightDigitWaIdGetsNoReply()
    {
        var before = await Everywhere();
        await Inbound("29022345", text: "hola");
        Assert.Empty(kapso);
        Assert.Equal(before, await Everywhere());
        await using var db = own.NewDb(new TenantScope());
        Assert.False(await db.GeneralReplyCaps.AnyAsync(c => c.PhoneHash == Hash("29022345")));
    }

    // ---------------- Criterios 24 y 26: free text, the cap and what is stored ----------------
    [Theory][InlineData("hola")][InlineData("quiero cambiar mi cita")][InlineData("ACTIVAR RECORDATORIOS")]
    public async Task FreeTextGetsOneFixedReplyPerDayAndLeavesOnlyTheCap(string text)
    {
        var before = await Everywhere();

        await Inbound(phone, text: text);
        await Inbound(phone, text: "¿hay alguien?");

        var (key, path, body) = Assert.Single(kapso); // the second one, within 24 h, gets nothing
        Assert.Equal((PlatformKey, $"/meta/whatsapp/v24.0/{PlatformNumber}/messages"), (key, path));
        Assert.Equal(FreeText, body.GetProperty("text").GetProperty("body").GetString());
        Assert.Equal("+" + phone, body.GetProperty("to").GetString()); // criterio 28: the wa_id with its «+»
        Assert.Equal(before, await Everywhere()); // no Contact, Conversation, Message or agent Job anywhere (INV-9, A17)
        await using var db = own.NewDb(new TenantScope());
        var cap = await db.GeneralReplyCaps.AsNoTracking().SingleAsync(c => c.PhoneHash == Hash(phone));
        Assert.Equal(["LastReplyAt", "PhoneHash"], typeof(GeneralReplyCap).GetProperties().Select(p => p.Name).Order()); // never a body
    }
    [Fact]
    public async Task TheCapExpiresAfterADay()
    {
        await Inbound(phone, text: "hola");
        await using (var db = own.NewDb(new TenantScope()))
            await db.GeneralReplyCaps.Where(c => c.PhoneHash == Hash(phone)).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastReplyAt, DateTimeOffset.UtcNow.AddHours(-25)));

        await Inbound(phone, text: "hola otra vez");

        Assert.Equal(2, kapso.Count);
        await using var read = own.NewDb(new TenantScope());
        Assert.True((await read.GeneralReplyCaps.AsNoTracking().SingleAsync(c => c.PhoneHash == Hash(phone))).LastReplyAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.False(await read.GeneralReplyCaps.AnyAsync(c => c.LastReplyAt < DateTimeOffset.UtcNow.AddHours(-24))); // expired rows do not stay
    }

    // ---------------- Criterios 25 y 19 (bajas), A14, A20 ----------------
    [Fact]
    public async Task BajaStopsEveryGeneralClinicWithdrawsWhereItWroteAndLeavesOwnNumbersAlone()
    {
        // A: one reminder already sent and one pending. B: only a pending one. Own number: the same phone, with its own consent.
        var sentA = await Sent(clinicA, appointmentA, patientA);
        var pendingA = await Sent(clinicA, appointmentA, patientA, "pending", window: "hour_before");
        var pendingB = await Sent(clinicB, appointmentB, patientB, "pending");
        var ownPhone = phone;
        await using (var db = own.NewContext()) { var c = await db.Contacts.SingleAsync(x => x.Id == own.Contact.Id); c.Phone = ownPhone; c.PhoneHash = Hash(ownPhone); c.ReminderConsentAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); }

        await Inbound(phone, text: "BAJA");

        var write = Assert.Single(hospitalWrites); // only where the general number wrote
        Assert.Equal((clinicA, $"/v1/patients/{patientA}/whatsapp-reminders/consent/withdraw"), (write.Tenant, write.Path));
        Assert.Equal("whatsapp-baja", JsonDocument.Parse(write.Body).RootElement.GetProperty("origin").GetString());
        Assert.Equal("cancelled", (await Row(clinicA, pendingA.Id)).Status);
        Assert.Equal("cancelled", (await Row(clinicB, pendingB.Id)).Status);
        var a = await Row(clinicA, sentA.Id);
        Assert.Equal(("sent", null), (a.Status, a.OptOutPendingAt)); Assert.NotNull(a.OptOutWithdrawnAt);
        await using (var db = own.NewContext()) Assert.NotNull((await db.Contacts.AsNoTracking().SingleAsync(x => x.Id == own.Contact.Id)).ReminderConsentAt);
        var (key, _, reply) = Assert.Single(kapso);
        Assert.Equal(PlatformKey, key);
        var text = reply.GetProperty("text").GetProperty("body").GetString()!;
        foreach (var name in new[] { "Clínica", "Pública", "Sintética", "1", "2" }) Assert.DoesNotContain(name, text);
        await using (var db = own.NewDb(new TenantScope())) Assert.True(await db.GeneralOptOuts.AnyAsync(o => o.PhoneHash == Hash(phone)));

        // From then on nothing is created or sent to that phone where the authorisation Hospital reports predates the BAJA (criterio 19).
        recipients[clinicB] = [Recipient(Guid.NewGuid(), patientB, start.AddDays(1), consentSince: DateTimeOffset.UtcNow.AddDays(-1))];
        await Sync(clinicB, DateTimeOffset.UtcNow.AddMinutes(16));
        Assert.DoesNotContain(await Rows(clinicB), r => r.Status == "pending");
        // In A the desk recorded the authorisation again after the BAJA (consentSince later): it counts again, for that clinic only (A20).
        recipients[clinicA] = [Recipient(Guid.NewGuid(), patientA, start.AddDays(1), consentSince: DateTimeOffset.UtcNow.AddMinutes(1))];
        await Sync(clinicA, DateTimeOffset.UtcNow.AddMinutes(16));
        Assert.Equal(2, (await Rows(clinicA)).Count(r => r.Status == "pending"));
    }
    [Fact]
    public async Task BajaWithHospitalDownBlocksSendingAndRetriesTheWithdrawalLater()
    {
        recipients[clinicA] = [Recipient(appointmentA, patientA, start)];
        await Sync(clinicA);
        var sent = await Sent(clinicA, Guid.NewGuid(), patientA, at: start.AddHours(1));
        var day = (await Rows(clinicA)).Single(r => r.Window == "day_before" && r.AppointmentId == appointmentA);
        hospitalDown = true;

        await Inbound(phone, text: "baja");

        Assert.Empty(hospitalWrites);
        Assert.NotNull((await Row(clinicA, sent.Id)).OptOutPendingAt);
        Assert.Equal("cancelled", (await Row(clinicA, day.Id)).Status); // the opt-out list already stops the send
        Assert.Single(kapso); // the fixed line still goes out

        hospitalDown = false;
        await using (var db = Db(clinicA)) await Service(clinicA, db, Kapso()).RetryOptOuts();
        await using (var db = Db(clinicA)) await Service(clinicA, db, Kapso()).RetryOptOuts(); // nothing left: no second call

        var write = Assert.Single(hospitalWrites);
        Assert.Equal($"/v1/patients/{patientA}/whatsapp-reminders/consent/withdraw", write.Path);
        var row = await Row(clinicA, sent.Id);
        Assert.Null(row.OptOutPendingAt); Assert.NotNull(row.OptOutWithdrawnAt);
    }
    [Fact]
    public async Task ADispatchToAnOptedOutPhoneIsCancelled()
    {
        recipients[clinicB] = [Recipient(appointmentB, patientB, start)];
        await Sync(clinicB);
        var day = (await Rows(clinicB)).Single(r => r.Window == "day_before");
        await using (var db = own.NewDb(new TenantScope())) { db.GeneralOptOuts.Add(new GeneralOptOut { PhoneHash = Hash(phone), CreatedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }

        await Dispatch(clinicB, day.Id, day.DueAt);

        Assert.Equal("cancelled", (await Row(clinicB, day.Id)).Status);
        Assert.Empty(kapso);
    }

    // ======================= Ronda 2 (revisión) =======================

    // Important 1 + Moderate 11: one unusable phone never stops the clinic. Criterio 18 (OQ-2): any country written with its «+» is written to;
    // a local number without its country code is not, because the general number cannot know the country.
    [Fact]
    public async Task AnUnusablePhoneSkipsOnlyItsRowsAndSaysWhy()
    {
        Guid bad = Guid.NewGuid(), local = Guid.NewGuid(), foreign = Guid.NewGuid();
        recipients[clinicA] = [Recipient(appointmentA, patientA, start), Recipient(bad, Guid.NewGuid(), start, "12"), Recipient(local, Guid.NewGuid(), start, "7000-0000"), Recipient(foreign, Guid.NewGuid(), start, "+1 415 555 0100")];

        await Sync(clinicA);

        var rows = await Rows(clinicA);
        Assert.Equal("Teléfono inválido o sin código de país; el número general no puede escribirle.", AppointmentReminderService.UnusablePhone);
        Assert.Equal(2, rows.Count(r => r.AppointmentId == appointmentA && r.Status == "pending"));
        Assert.All(rows.Where(r => r.AppointmentId == bad || r.AppointmentId == local), r => Assert.Equal(("skipped", AppointmentReminderService.UnusablePhone, (string?)null), (r.Status, r.Reason, r.RecipientHash)));
        Assert.Equal(4, rows.Count(r => r.AppointmentId == bad || r.AppointmentId == local));
        Assert.All(rows.Where(r => r.AppointmentId == foreign), r => Assert.Equal(("pending", (string?)null, Hash("14155550100")), (r.Status, r.Reason, r.RecipientHash)));
        Assert.Equal(2, rows.Count(r => r.AppointmentId == foreign));
        await using (var db = Db(clinicA)) Assert.Contains("2 cita(s)", (await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == clinicA)).ReminderSyncError);
        // The day's re-read before the send has the bad phones in it too; the good ones still go out, the foreign one to its own digits.
        foreach (var id in new[] { appointmentA, foreign })
        {
            var day = rows.Single(r => r.AppointmentId == id && r.Window == "day_before");
            await Dispatch(clinicA, day.Id, day.DueAt);
            Assert.Equal("sent", (await Row(clinicA, day.Id)).Status);
        }
        Assert.Contains(kapso, k => k.Body.GetProperty("to").GetString() == "+14155550100"); // criterio 28
        Assert.DoesNotContain(kapso, k => k.Body.GetProperty("to").GetString()!.TrimStart('+') is "12" or "70000000" or "50370000000");
    }

    // TEL-1 criterio 21, branch (A), measured 2026-10-25: Kapso answers each send with contacts[].wa_id, so the general number writes to any
    // country (criterios 21, 31); outside the closed list a BAJA matches by the wa_id's hash (criterio 22). Changed by that decision: under
    // branch (B) Mexico was skipped with OutsideList.
    [Fact]
    public async Task BranchAWritesToAnyCountryWithItsCode()
    {
        Guid us = Guid.NewGuid(), mexico = Guid.NewGuid();
        recipients[clinicA] = [Recipient(appointmentA, patientA, start, "+" + phone), Recipient(us, Guid.NewGuid(), start, "+1 415 555 0100"), Recipient(mexico, Guid.NewGuid(), start, "+52 55 1234 5678")];

        await Sync(clinicA);

        var rows = await Rows(clinicA);
        Assert.Equal("País fuera de la lista del número general (+503, +502, +504, +1).", AppointmentReminderService.OutsideList); // criterio 31
        Assert.All(rows.Where(r => r.AppointmentId == appointmentA), r => Assert.Equal(("pending", Hash(phone)), (r.Status, r.RecipientHash)));
        Assert.All(rows.Where(r => r.AppointmentId == us), r => Assert.Equal(("pending", Hash("14155550100")), (r.Status, r.RecipientHash)));
        Assert.All(rows.Where(r => r.AppointmentId == mexico), r => Assert.Equal(("pending", (string?)null, Hash("525512345678")), (r.Status, r.Reason, r.RecipientHash)));
        Assert.Equal(6, rows.Count);
    }

    // Criterio 22: the row keeps the HMAC of the wa_id the send came back with (Mexico: E.164 +52 55…, wa_id 521 55…), never the wa_id itself.
    [Fact]
    public async Task ADispatchStoresOnlyTheHashOfTheWaIdKapsoAnswers()
    {
        waIds["525512345678"] = "5215512345678";
        recipients[clinicA] = [Recipient(appointmentA, patientA, start, "+52 55 1234 5678")];
        await Sync(clinicA);
        var day = (await Rows(clinicA)).Single(r => r.Window == "day_before");

        await Dispatch(clinicA, day.Id, day.DueAt);

        var row = await Row(clinicA, day.Id);
        Assert.Equal(("sent", Hash("525512345678"), Hash("5215512345678")), (row.Status, row.RecipientHash, row.WaIdHash));
        Assert.Equal("+525512345678", Assert.Single(kapso).Body.GetProperty("to").GetString()); // criterio 28: «to» is the E.164, not the wa_id
        var stored = JsonSerializer.Serialize(row);
        foreach (var clear in new[] { "5215512345678", "525512345678" }) Assert.DoesNotContain(clear, stored);
    }

    // Criterio 23, the safety net of branch (A): a send outside +503/+502/+504/+1 that comes back without its wa_id stays «sent» with a
    // readable Activity, and the next reminders to that recipient are skipped: a BAJA from an unknown wa_id could not stop them (INV-8).
    [Fact]
    public async Task AForeignSendWithoutItsWaIdStopsTheNextOnes()
    {
        waIds["525512345678"] = null;
        Guid next = Guid.NewGuid();
        recipients[clinicA] = [Recipient(appointmentA, patientA, start, "+52 55 1234 5678")];
        await Sync(clinicA);
        var rows = await Rows(clinicA);
        var day = rows.Single(r => r.Window == "day_before"); var hour = rows.Single(r => r.Window == "hour_before");

        await Dispatch(clinicA, day.Id, day.DueAt);
        await Dispatch(clinicA, hour.Id, hour.DueAt);
        recipients[clinicA] = [Recipient(appointmentA, patientA, start, "+52 55 1234 5678"), Recipient(next, patientA, start.AddDays(1), "+52 55 1234 5678")];
        await Sync(clinicA, DateTimeOffset.UtcNow.AddMinutes(16));

        Assert.Equal(("sent", (string?)null), ((await Row(clinicA, day.Id)).Status, (await Row(clinicA, day.Id)).WaIdHash));
        await using (var db = own.NewDb(new TenantScope()))
            Assert.Equal("No se pudo confirmar el número de WhatsApp; los siguientes recordatorios a este paciente se detienen.",
                (await db.Activities.IgnoreQueryFilters().SingleAsync(a => a.TenantId == clinicA && a.Kind == "general_wa_id_missing")).Body);
        Assert.Equal(("skipped", AppointmentReminderService.OutsideList), ((await Row(clinicA, hour.Id)).Status, (await Row(clinicA, hour.Id)).Reason));
        var later = (await Rows(clinicA)).Where(r => r.AppointmentId == next).ToList();
        Assert.Equal(2, later.Count);
        Assert.All(later, r => Assert.Equal(("skipped", AppointmentReminderService.OutsideList), (r.Status, r.Reason)));
        Assert.Single(kapso); // only the first one went out
    }

    // ---------------- TEL-1 criterio 22: a BAJA or CANCELAR from the wa_id of the send (Mexico: E.164 +52 55…, wa_id 521 55…) ----------------
    // Mexico's shape (E.164 52 55 + 8 digits, wa_id 521 55 + the same 8), with random digits: the opt-out list and the BAJA are global (A17).
    readonly string MexicoDigits = Random.Shared.Next(10000000, 99999999).ToString(CultureInfo.InvariantCulture);
    string MexicoE164 => "+52 55 " + MexicoDigits;
    string MexicoWaId => "52155" + MexicoDigits;
    /// <summary>A day-before reminder sent by the general number to Mexico, Kapso answering with the wa_id that differs from the E.164.</summary>
    async Task<AppointmentReminder> SentToMexico()
    {
        waIds["5255" + MexicoDigits] = MexicoWaId;
        recipients[clinicA] = [Recipient(appointmentA, patientA, start, MexicoE164)];
        await Sync(clinicA);
        var day = (await Rows(clinicA)).Single(r => r.Window == "day_before");
        await Dispatch(clinicA, day.Id, day.DueAt);
        return await Row(clinicA, day.Id);
    }
    async Task ForgetMexico() { await using var db = own.NewDb(new TenantScope()); db.GeneralOptOuts.RemoveRange(db.GeneralOptOuts.Where(o => o.PhoneHash == Hash(MexicoWaId))); await db.SaveChangesAsync(); } // the list is global

    // Criterio 22 (a): CANCELAR from the wa_id resolves the row, and Hospital's re-read is compared with the row's RecipientHash, never with
    // the sender's hash (anti-criterio): compared with the sender's, this very case would not resolve.
    [Fact]
    public async Task ACancelFromTheWaIdOfTheSendCancelsInHospital()
    {
        var day = await SentToMexico();

        await Inbound(MexicoWaId, $"CANCELAR {day.Id}");
        var ask = kapso[^1].Body;
        Assert.Equal("interactive", ask.GetProperty("type").GetString());
        Assert.Equal([($"CANCELAR-SI {day.Id}", "Sí, cancelar"), ($"CANCELAR-NO {day.Id}", "No")], AgentHarness.Options(ask.GetProperty("interactive")));
        await Inbound(MexicoWaId, $"CANCELAR-SI {day.Id}");

        var write = Assert.Single(hospitalWrites);
        Assert.Equal((clinicA, $"/v1/agenda/{appointmentA}/cancel"), (write.Tenant, write.Path));
        Assert.Equal("cancelled", (await Row(clinicA, day.Id)).Status);
    }
    // Criterio 22 (a), guard: Hospital now names another phone for that appointment ⇒ the wa_id of the old send resolves nothing.
    [Fact]
    public async Task ACancelFromTheWaIdIsCheckedAgainstTheRowsRecipient()
    {
        var day = await SentToMexico();
        recipients[clinicA] = [Recipient(appointmentA, patientA, start, "+52 55 9999 0000")];

        await Inbound(MexicoWaId, $"CANCELAR {day.Id}");

        Assert.Equal(FreeText, kapso[^1].Body.GetProperty("text").GetProperty("body").GetString());
        Assert.Empty(hospitalWrites);
        Assert.Equal("sent", (await Row(clinicA, day.Id)).Status);
    }
    // Criterio 22 (b) and (c), INV-8: BAJA from the wa_id cancels what is pending, asks Hospital to withdraw the authorisation and goes on the
    // list; while Hospital still lists the patient with an authorisation from before the BAJA, nothing new is created or sent.
    [Fact]
    public async Task ABajaFromTheWaIdOfTheSendStopsTheRecipient()
    {
        var day = await SentToMexico();
        var hour = (await Rows(clinicA)).Single(r => r.Window == "hour_before");
        try
        {
            await Inbound(MexicoWaId, text: "BAJA");

            Assert.Equal("cancelled", (await Row(clinicA, hour.Id)).Status);
            var write = Assert.Single(hospitalWrites);
            Assert.Equal((clinicA, $"/v1/patients/{patientA}/whatsapp-reminders/consent/withdraw"), (write.Tenant, write.Path));
            Assert.NotNull((await Row(clinicA, day.Id)).OptOutWithdrawnAt);

            var next = Guid.NewGuid();
            recipients[clinicA] = [Recipient(next, patientA, start.AddDays(1), MexicoE164, consentSince: DateTimeOffset.UtcNow.AddDays(-1))];
            await Sync(clinicA, DateTimeOffset.UtcNow.AddMinutes(16));

            Assert.DoesNotContain(await Rows(clinicA), r => r.Status == "pending");
            Assert.DoesNotContain(await Rows(clinicA), r => r.AppointmentId == next); // no new row at all (22c)
            Assert.Single(kapso, k => k.Body.TryGetProperty("template", out _)); // only the first reminder ever went out
        }
        finally { await ForgetMexico(); }
    }
    // Criterio 22b across hospitals (review nit 2): a BAJA from the wa_id clinic A's send came back with also stops clinic B's rows of the same
    // recipient, which carry no WaIdHash: pending ones are cancelled and the one B sent asks B's Hospital to withdraw.
    [Fact]
    public async Task ABajaFromTheWaIdStopsTheSameRecipientInAnotherClinic()
    {
        await SentToMexico();
        var pendingB = await Sent(clinicB, appointmentB, patientB, "pending", to: MexicoE164, window: "hour_before");
        var sentB = await Sent(clinicB, appointmentB, patientB, to: MexicoE164);
        try
        {
            await Inbound(MexicoWaId, text: "BAJA");

            Assert.Equal("cancelled", (await Row(clinicB, pendingB.Id)).Status);
            Assert.NotNull((await Row(clinicB, sentB.Id)).OptOutWithdrawnAt);
            Assert.Contains(hospitalWrites, w => w.Tenant == clinicB && w.Path == $"/v1/patients/{patientB}/whatsapp-reminders/consent/withdraw");
        }
        finally { await ForgetMexico(); }
    }
    // Review Important 1: the safety net of criterio 23 holds across hospitals, like the BAJA. Clinic A's send came back without its wa_id, the
    // patient asked BAJA from a wa_id nobody can match: clinic B does not write either.
    [Fact]
    public async Task AForeignSendWithoutItsWaIdInOneClinicStopsTheOthers()
    {
        waIds["5255" + MexicoDigits] = null;
        recipients[clinicA] = [Recipient(appointmentA, patientA, start, MexicoE164)];
        await Sync(clinicA);
        var dayA = (await Rows(clinicA)).Single(r => r.Window == "day_before");
        await Dispatch(clinicA, dayA.Id, dayA.DueAt);
        try
        {
            await Inbound(MexicoWaId, text: "BAJA");
            recipients[clinicB] = [Recipient(appointmentB, patientB, start, MexicoE164)];

            await Sync(clinicB);
            foreach (var row in await Rows(clinicB)) await Dispatch(clinicB, row.Id, row.DueAt);

            var rowsB = await Rows(clinicB);
            Assert.Equal(2, rowsB.Count);
            Assert.All(rowsB, r => Assert.Equal(("skipped", AppointmentReminderService.OutsideList), (r.Status, r.Reason)));
            Assert.Single(kapso, k => k.Body.TryGetProperty("template", out _)); // only clinic A's
        }
        finally { await ForgetMexico(); }
    }
    // Review Important 1: a BAJA from a wa_id that no send had returned yet matches nothing; when a later send comes back with that wa_id, the
    // patient is known to have asked BAJA: the withdrawal is asked of Hospital (OptOutPendingAt, the BAJA's moment) and nothing more goes out.
    [Fact]
    public async Task ASendWhoseWaIdAlreadyAskedBajaWithdrawsTheAuthorisation()
    {
        waIds["5255" + MexicoDigits] = MexicoWaId;
        try
        {
            await Inbound(MexicoWaId, text: "BAJA");
            Assert.Empty(hospitalWrites);
            recipients[clinicB] = [Recipient(appointmentB, patientB, start, MexicoE164)];
            await Sync(clinicB);
            var rows = await Rows(clinicB);
            var day = rows.Single(r => r.Window == "day_before"); var hour = rows.Single(r => r.Window == "hour_before");

            await Dispatch(clinicB, day.Id, day.DueAt); // nothing linked the wa_id to this recipient before this send
            Assert.NotNull((await Row(clinicB, day.Id)).OptOutPendingAt);
            await Dispatch(clinicB, hour.Id, hour.DueAt);
            await using (var db = Db(clinicB)) await Service(clinicB, db, Kapso()).RetryOptOuts();

            Assert.Equal("cancelled", (await Row(clinicB, hour.Id)).Status);
            Assert.Single(kapso, k => k.Body.TryGetProperty("template", out _));
            var write = Assert.Single(hospitalWrites);
            Assert.Equal((clinicB, $"/v1/patients/{patientB}/whatsapp-reminders/consent/withdraw"), (write.Tenant, write.Path));
        }
        finally { await ForgetMexico(); }
    }
    // Review nit 1: once Kapso accepted the send, nothing after it uses the worker's token. A worker stopping right then must not turn a
    // delivered reminder (and its wa_id's hash) into a doubt.
    [Fact]
    public async Task AWorkerStoppingRightAfterTheSendKeepsItSent()
    {
        waIds["5255" + MexicoDigits] = MexicoWaId;
        recipients[clinicA] = [Recipient(appointmentA, patientA, start, MexicoE164)];
        await Sync(clinicA);
        var day = (await Rows(clinicA)).Single(r => r.Window == "day_before");
        using var stop = new CancellationTokenSource();
        var afterTheSend = new StopAfter(() => kapso.Any(k => k.Body.TryGetProperty("template", out _)), stop);

        await using (var db = own.NewDb(new TenantScope { Id = clinicA }, afterTheSend)) await Service(clinicA, db, Kapso()).Dispatch(day.Id, day.DueAt, stop.Token);

        Assert.True(stop.IsCancellationRequested); // the worker did stop after the send
        var row = await Row(clinicA, day.Id);
        Assert.Equal(("sent", Hash(MexicoWaId)), (row.Status, row.WaIdHash));
    }
    /// <summary>Cancels the worker's token at the first database read once <paramref name="when"/> holds.</summary>
    sealed class StopAfter(Func<bool> when, CancellationTokenSource stop) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (when()) stop.Cancel();
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    // Criterio 22, regression: without the wa_id's hash (a row sent before it was stored), a BAJA from a different wa_id does not match, as before.
    [Fact]
    public async Task ABajaFromADifferentWaIdWithoutItsHashDoesNotStop()
    {
        await Sent(clinicA, appointmentA, patientA, to: MexicoE164);
        var pending = await Sent(clinicA, appointmentA, patientA, "pending", to: MexicoE164, window: "hour_before");
        try
        {
            await Inbound(MexicoWaId, text: "BAJA");

            Assert.Equal("pending", (await Row(clinicA, pending.Id)).Status);
            Assert.Empty(hospitalWrites);
        }
        finally { await ForgetMexico(); }
    }

    // Criterio 23 (guard): inside the closed list the wa_id is the E.164, so a send without contacts[] stops nothing.
    [Fact]
    public async Task AListedSendWithoutItsWaIdStopsNothing()
    {
        waIds["14155550100"] = null;
        recipients[clinicA] = [Recipient(appointmentA, patientA, start, "+1 415 555 0100")];
        await Sync(clinicA);
        var rows = await Rows(clinicA);

        foreach (var row in rows) await Dispatch(clinicA, row.Id, row.DueAt);

        Assert.All(await Rows(clinicA), r => Assert.Equal("sent", r.Status));
        Assert.Equal(2, kapso.Count);
    }

    // Criterio 31: the closed list is +503, +502, +504 and +1; Guatemala and Honduras are written to, and a BAJA from their wa_id stops them.
    [Theory][InlineData("+502 2345 6789", "50223456789")][InlineData("+504 9999 9999", "50499999999")]
    public async Task GuatemalaAndHondurasAreInTheList(string e164, string waId)
    {
        recipients[clinicA] = [Recipient(appointmentA, patientA, start, e164)];
        await Sync(clinicA);
        Assert.All(await Rows(clinicA), r => Assert.Equal(("pending", Hash(waId)), (r.Status, r.RecipientHash)));
        try
        {
            await Inbound(waId, text: "BAJA"); // the patient answers from WhatsApp: the wa_id, without «+»

            Assert.All(await Rows(clinicA), r => Assert.Equal("cancelled", r.Status));
            Assert.DoesNotContain(kapso, k => k.Body.TryGetProperty("template", out _)); // no reminder goes out
        }
        finally
        {
            await using var db = own.NewDb(new TenantScope());
            db.GeneralOptOuts.RemoveRange(db.GeneralOptOuts.Where(o => o.PhoneHash == Hash(waId))); await db.SaveChangesAsync(); // the list is global
        }
    }

    // Criterio 23: a row skipped for its country (branch B, or the safety net of branch A) reopens once Hospital names a phone the general number
    // writes to. Seeded as skipped: under branch (A) a sync no longer skips +52 by itself, so syncing it first would pass without reopening anything.
    [Fact]
    public async Task ARowSkippedForItsCountryReopensWhenThePhoneIsInTheList()
    {
        Guid appointment = Guid.NewGuid(), patient = Guid.NewGuid();
        await using (var db = Db(clinicA))
        {
            db.AppointmentReminders.Add(new() { TenantId = clinicA, PatientId = patient, AppointmentId = appointment, StartsAt = start, DueAt = start.AddDays(-1), Window = "day_before", CreatedAt = DateTimeOffset.UtcNow,
                Status = "skipped", Reason = AppointmentReminderService.OutsideList });
            await db.SaveChangesAsync();
        }
        recipients[clinicA] = [Recipient(appointment, patient, start, "+1 415 555 0100")];

        await Sync(clinicA);

        var day = (await Rows(clinicA)).Single(r => r.Window == "day_before");
        Assert.Equal(("pending", (string?)null, Hash("14155550100")), (day.Status, day.Reason, day.RecipientHash));
    }

    // Criterios 18 and 23: a row still skipped says why today. One skipped with the text before OQ-2 whose phone still has no country code now
    // says UnusablePhone (under branch A a +52 phone is written to, so it would reopen instead).
    [Fact]
    public async Task ASkippedRowShowsTheCurrentReason()
    {
        Guid mexico = Guid.NewGuid(), patient = Guid.NewGuid();
        await using (var db = Db(clinicA))
        {
            db.AppointmentReminders.Add(new() { TenantId = clinicA, PatientId = patient, AppointmentId = mexico, StartsAt = start, DueAt = start.AddDays(-1), Window = "day_before", CreatedAt = DateTimeOffset.UtcNow,
                Status = "skipped", Reason = "Teléfono inválido o fuera de El Salvador (+503); el número general aún no lo admite." });
            await db.SaveChangesAsync();
        }
        recipients[clinicA] = [Recipient(mexico, patient, start, "7000-0000")]; // criterio 21 (A): +52 is written to now; a phone without its code is not

        await Sync(clinicA);

        var day = (await Rows(clinicA)).Single(r => r.AppointmentId == mexico && r.Window == "day_before");
        Assert.Equal(("skipped", AppointmentReminderService.UnusablePhone, (string?)null), (day.Status, day.Reason, day.RecipientHash));
    }

    // Criterio 18: a row skipped with the text used before OQ-2 reopens too, once Hospital's phone carries its country code.
    [Fact]
    public async Task ASkippedRowWithThePreviousTextReopensWhenThePhoneHasACountryCode()
    {
        Guid foreign = Guid.NewGuid(), patient = Guid.NewGuid();
        await using (var db = Db(clinicA))
        {
            db.AppointmentReminders.Add(new() { TenantId = clinicA, PatientId = patient, AppointmentId = foreign, StartsAt = start, DueAt = start.AddDays(-1), Window = "day_before", CreatedAt = DateTimeOffset.UtcNow,
                Status = "skipped", Reason = "Teléfono inválido o fuera de El Salvador (+503); el número general aún no lo admite." });
            await db.SaveChangesAsync();
        }
        recipients[clinicA] = [Recipient(foreign, patient, start, "+1 415 555 0100")];

        await Sync(clinicA);

        var day = (await Rows(clinicA)).Single(r => r.AppointmentId == foreign && r.Window == "day_before");
        Assert.Equal(("pending", (string?)null, Hash("14155550100")), (day.Status, day.Reason, day.RecipientHash));
    }

    // Criterio 17, INV-4: a BAJA sent from a foreign wa_id stops the same number when Hospital writes it in E.164.
    [Fact]
    public async Task ABajaFromAForeignWaIdStopsTheHospitalE164Recipient()
    {
        // Its own number: the opt-out list is global, and 14155550100 is written to by the other tests of this class.
        var waId = "1415" + Random.Shared.Next(1000000, 9999999).ToString(CultureInfo.InvariantCulture);
        await using (var db = own.NewDb(new TenantScope())) { db.GeneralOptOuts.Add(new GeneralOptOut { PhoneHash = Hash(waId), CreatedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        recipients[clinicA] = [Recipient(appointmentA, patientA, start, "+" + waId)];

        await Sync(clinicA);

        Assert.Empty(await Rows(clinicA));
        Assert.Empty(kapso);
    }

    // Criterio 17, INV-4 (guard: green before the change too): a BAJA from the wa_id stops Hospital's +503 number written with its «+».
    [Fact]
    public async Task ABajaFromTheWaIdStopsTheSameNumberWithPlus()
    {
        await using (var db = own.NewDb(new TenantScope())) { db.GeneralOptOuts.Add(new GeneralOptOut { PhoneHash = Hash(phone), CreatedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        recipients[clinicA] = [Recipient(appointmentA, patientA, start, "+" + phone)];

        await Sync(clinicA);

        Assert.Empty(await Rows(clinicA));
        Assert.Empty(kapso);
    }

    // Important 2: the hour before on the general number has its own template, with the clinic's phone and no «responde».
    [Fact]
    public async Task TheHourBeforeGoesWithTheGeneralHourTemplate()
    {
        recipients[clinicA] = [Recipient(appointmentA, patientA, start)];
        await Sync(clinicA);
        var hour = (await Rows(clinicA)).Single(r => r.Window == "hour_before");

        await Dispatch(clinicA, hour.Id, hour.DueAt);

        var (key, _, body) = Assert.Single(kapso);
        Assert.Equal(PlatformKey, key);
        var template = body.GetProperty("template");
        Assert.Equal(KapsoClient.GeneralHourTemplate, template.GetProperty("name").GetString());
        var values = template.GetProperty("components")[0].GetProperty("parameters").EnumerateArray().ToDictionary(p => p.GetProperty("parameter_name").GetString()!, p => p.GetProperty("text").GetString());
        Assert.Equal(("Clínica Pública A", "+503 2200 0001"), (values["hospital"], values["telefono"]));
        Assert.DoesNotContain(template.GetProperty("components").EnumerateArray(), c => c.GetProperty("type").GetString() == "button");
    }
    [Fact]
    public void TheGeneralHourTemplateInTheRepoIsValidOnlyForItsWindow()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "integrations", "kapso", "templates"))) root = root.Parent;
        var template = JsonNode.Parse(File.ReadAllText(Path.Combine(root!.FullName, "integrations", "kapso", "templates", "general_una_hora.json")))!.AsObject();
        Assert.Equal(KapsoClient.GeneralHourTemplate, (string?)template["name"]);
        Assert.DoesNotContain("respond", template.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BAJA", (string?)template["components"]!.AsArray().Single(c => (string?)c!["type"] == "FOOTER")!["text"]);
        template["status"] = "APPROVED"; var json = JsonSerializer.SerializeToElement(template);
        Assert.True(KapsoClient.ValidReminderTemplate(json, KapsoClient.GeneralHourWindow));
        Assert.False(KapsoClient.ValidReminderTemplate(json, "hour_before"));
    }

    // Moderate 5: a BAJA withdraws in every clinic the general number wrote to, even one that moved to Recepción since; the worker retries it there.
    [Fact]
    public async Task ABajaWithdrawsWhereTheGeneralNumberWroteEvenAfterMovingToRecepcion()
    {
        var sent = await Sent(clinicA, appointmentA, patientA);
        var scope = new TenantScope();
        await using (var db = own.NewDb(scope)) Assert.Equal(204, Status(await PlatformEndpoints.Delete(clinicA, Principal("\"platform-owner\""), db, scope, default)));
        hospitalDown = true;

        await Inbound(phone, text: "BAJA");
        Assert.NotNull((await Row(clinicA, sent.Id)).OptOutPendingAt);

        hospitalDown = false;
        await AppointmentReminderWorker.RunTenant(Scopes(), clinicA, NullLogger.Instance, default);

        var write = Assert.Single(hospitalWrites);
        Assert.Equal((clinicA, $"/v1/patients/{patientA}/whatsapp-reminders/consent/withdraw"), (write.Tenant, write.Path));
        Assert.Null((await Row(clinicA, sent.Id)).OptOutPendingAt);
    }
    /// <summary>The worker's scopes, as Hosting registers them, over this test's doubles.</summary>
    IServiceScopeFactory Scopes()
    {
        var services = new ServiceCollection();
        services.AddScoped<TenantScope>(); services.AddScoped(sp => own.NewDb(sp.GetRequiredService<TenantScope>()));
        services.AddScoped(sp =>
        {
            var config = Config(); var scope = sp.GetRequiredService<TenantScope>(); var db = sp.GetRequiredService<CrmDb>(); var client = new KapsoClient(new HttpClient(Kapso()), config);
            return new AppointmentReminderService(db, scope, new HospitalClient(new HttpClient(Hospital()), config), client, new ConversationService(db, scope, client), config);
        });
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
    // Test gap: the worker reads a general-number clinic at most every 15 minutes, and a paused one not at all.
    [Fact]
    public async Task TheWorkerReadsAGeneralClinicEveryFifteenMinutesAndAPausedOneNever()
    {
        recipients[clinicA] = [Recipient(appointmentA, patientA, start)]; recipients[clinicB] = [Recipient(appointmentB, patientB, start)];
        await using (var db = Db(clinicB)) await db.Tenants.Where(t => t.Id == clinicB).ExecuteUpdateAsync(x => x.SetProperty(t => t.RemindersEnabled, false));

        await AppointmentReminderWorker.RunTenant(Scopes(), clinicA, NullLogger.Instance, default);
        await AppointmentReminderWorker.RunTenant(Scopes(), clinicA, NullLogger.Instance, default);
        await AppointmentReminderWorker.RunTenant(Scopes(), clinicB, NullLogger.Instance, default);

        Assert.Equal(1, hospitalReads.Count(p => p == "/v1/agenda/whatsapp-reminders"));
        Assert.Equal(2, (await Rows(clinicA)).Count); Assert.Empty(await Rows(clinicB));
    }

    // Moderate 6: ten BAJA are one withdrawal and one line; a send that failed never reached the phone and asks for nothing.
    [Fact]
    public async Task TenBajaAreOneWithdrawalAndOneLine()
    {
        await Sent(clinicA, appointmentA, patientA);
        await Sent(clinicB, appointmentB, patientB, "failed");

        for (var i = 0; i < 10; i++) await Inbound(phone, text: "BAJA");

        var write = Assert.Single(hospitalWrites);
        Assert.Equal(clinicA, write.Tenant);
        Assert.Single(kapso);
    }

    // Moderate 8: the hospital's own time zone comes from Hospital's list.
    [Fact]
    public async Task ProvisioningTakesTheHospitalsTimeZone()
    {
        var id = Guid.NewGuid();
        Assert.Equal(200, Status(await Provision(id, Principal("\"platform-owner\""), Listing("America/Mexico_City", id))));
        await using var db = own.NewDb(new TenantScope());
        Assert.Equal("America/Mexico_City", (await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == id)).TimeZone);
    }

    // Moderate 9: for a general-number clinic the platform owner operates only its Hospital connection; the lists say which mode each one is.
    // One built, never started, never disposed host for the class (EF keeps the first data protection provider it sees: WhatsAppWebhookTests).
    static readonly Lazy<(RouteEndpoint[] Endpoints, IServiceProvider Services)> Api = new(() =>
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = Environment.GetEnvironmentVariable("TEST_DATABASE"), ["Auth:Authority"] = "https://identity.example.test/realms/hospital", ["KEY_DIRECTORY"] = Path.GetTempPath(), ["KAPSO_PLATFORM_PHONE_NUMBER_ID"] = PlatformNumber });
        builder.Services.AddRecepcionServices(builder.Configuration, false);
        var app = builder.Build(); app.MapRecepcionApi();
        return (((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>().ToArray(), app.Services);
    });
    static RouteEndpoint Route(string method, string pattern) => Api.Value.Endpoints.Single(e => e.RoutePattern.RawText == pattern && e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));
    DefaultHttpContext Acting(Endpoint endpoint, Guid tenant) { var ctx = Principal("\"platform-owner\""); ctx.Request.Headers[PlatformOwner.Header] = tenant.ToString(); ctx.SetEndpoint(endpoint); return ctx; }
    [Fact]
    public async Task TheOwnerOperatesOnlyTheHospitalConnectionOfAGeneralClinic()
    {
        await using var db = own.NewDb(new TenantScope());
        foreach (var (method, pattern) in new[] { ("POST", "/api/channels/onboarding"), ("POST", "/api/channels/sync"), ("GET", "/api/channels"), ("GET", "/api/platform/installation") })
            Assert.Equal(403, await PlatformOwner.Gate(Acting(Route(method, pattern), clinicA), db, new TenantScope(), new CurrentUser()));
        foreach (var (method, pattern) in new[] { ("GET", "/api/hospital/connection"), ("PUT", "/api/hospital/connection"), ("POST", "/api/hospital/connection/check") })
            Assert.Equal(0, await PlatformOwner.Gate(Acting(Route(method, pattern), clinicA), db, new TenantScope(), new CurrentUser()));
        Assert.Equal(0, await PlatformOwner.Gate(Acting(Route("POST", "/api/channels/sync"), own.Scope.Id), db, new TenantScope(), new CurrentUser())); // a reception, as before
        var listed = await PlatformOwner.Tenants(new CurrentUser { Role = "platform", Subject = "platform:" + sub }, db, new HospitalClient(new HttpClient(), new ConfigurationBuilder().Build()));
        Assert.Equal(("reminders", "reception"), (listed.Tenants.Single(t => t.Id == clinicA).Mode, listed.Tenants.Single(t => t.Id == own.Scope.Id).Mode));
    }

    // Moderate 10: the owner's list of general-number sends nobody else will look at: states only.
    [Fact]
    public async Task TheOwnerSeesTheGeneralNumbersDoubtsAsStatesOnly()
    {
        var uncertain = await Sent(clinicA, appointmentA, patientA, "uncertain");
        var cancelling = await Sent(clinicB, appointmentB, patientB, "cancelling");
        await Sent(clinicA, Guid.NewGuid(), patientA); // sent: not a doubt
        var scope = new TenantScope(); await using var db = own.NewDb(scope);

        await Assert.ThrowsAsync<AccessDeniedException>(() => PlatformEndpoints.Doubts(Principal("\"Administrador\"", clinicA), db, new TenantScope(), default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => PlatformEndpoints.Doubts(Principal("\"platform-owner\"", clinicA), db, new TenantScope(), default));
        var doubts = (await PlatformEndpoints.Doubts(Principal("\"platform-owner\""), db, scope, default)).Where(d => d.TenantId == clinicA || d.TenantId == clinicB).ToList();

        Assert.Equal(new[] { uncertain.Id, cancelling.Id }.Order(), doubts.Select(d => d.Id).Order());
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(doubts, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.All(wire.RootElement.EnumerateArray(), row => Assert.Equal(["id", "tenantId", "startsAt", "status", "reason"], row.EnumerateObject().Select(x => x.Name)));
    }

    // Test gap: Hospital refusing (4xx) or failing (5xx) the cancellation.
    [Theory][InlineData(HttpStatusCode.Conflict, "sent")][InlineData(HttpStatusCode.ServiceUnavailable, "cancelling")]
    public async Task ACancellationHospitalDoesNotConfirmIsNeverReportedAsDone(HttpStatusCode answer, string status)
    {
        recipients[clinicA] = [Recipient(appointmentA, patientA, start)];
        var row = await Sent(clinicA, appointmentA, patientA); cancelAnswer = answer;

        await Inbound(phone, $"CANCELAR-SI {row.Id}");

        Assert.Single(hospitalWrites);
        Assert.Equal(status, (await Row(clinicA, row.Id)).Status);
        Assert.Equal("No pudimos confirmar la cancelación. Por favor llame a su clínica.", Assert.Single(kapso).Body.GetProperty("text").GetProperty("body").GetString());
        var scope = new TenantScope(); await using var db = own.NewDb(scope);
        Assert.Equal(status == "cancelling", (await PlatformEndpoints.Doubts(Principal("\"platform-owner\""), db, scope, default)).Any(d => d.Id == row.Id));
    }

    // Test gaps: another number's webhook, and Kapso redelivering the same message.
    [Fact]
    public async Task AMessageForAnotherNumberIsIgnored()
    {
        await Inbound(phone, text: "hola", number: "another-number");
        Assert.Empty(kapso);
        await using var db = own.NewDb(new TenantScope());
        Assert.False(await db.GeneralReplyCaps.AnyAsync(c => c.PhoneHash == Hash(phone)));
    }
    [Fact]
    public async Task ARedeliveredTapIsAnsweredOnce()
    {
        recipients[clinicA] = [Recipient(appointmentA, patientA, start)];
        var row = await Sent(clinicA, appointmentA, patientA); var id = "wamid." + Guid.NewGuid().ToString("N");
        await Inbound(phone, $"CANCELAR {row.Id}", messageId: id);
        await Inbound(phone, $"CANCELAR {row.Id}", messageId: id);
        Assert.Single(kapso);
    }

    // Nit: the platform's account is never a clinic's; the start fails when they are the same.
    [Theory][InlineData("KAPSO_PLATFORM_API_KEY", "KAPSO_API_KEY")][InlineData("KAPSO_PLATFORM_WEBHOOK_SECRET", "KAPSO_WEBHOOK_SECRET")]
    public void TheStartRefusesPlatformCredentialsEqualToAClinics(string platform, string clinic)
    {
        Assert.Throws<InvalidOperationException>(() => GeneralNumber.RequireSeparateCredentials(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [platform] = "same-value", [clinic] = "same-value" }).Build()));
        GeneralNumber.RequireSeparateCredentials(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [platform] = "platform-value", [clinic] = "clinic-value" }).Build());
    }

    // Test gap: PUT/DELETE/doubts through their mapped endpoints (route binding, authorization metadata and the Errors middleware).
    [Theory][InlineData("PUT")][InlineData("DELETE")][InlineData("GET")]
    public async Task ThePlatformRoutesRequireAuthenticationAndRefuseAHospitalUser(string method)
    {
        var endpoint = Route(method, method == "GET" ? "/platform/reminder-clinics/doubts" : "/platform/reminder-clinics/{tenantId:guid}");
        Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>());
        foreach (var who in new[] { Principal("\"Administrador\"", clinicA), Principal("\"platform-owner\"", clinicA) })
        {
            using var services = Api.Value.Services.CreateScope(); // the host's services resolve the handler's parameters
            who.RequestServices = services.ServiceProvider; who.Request.Method = method; who.Response.Body = new MemoryStream();
            who.Request.RouteValues["tenantId"] = clinicA.ToString();
            if (method == "PUT") { var body = Encoding.UTF8.GetBytes("{\"enabled\":true}"); who.Request.ContentType = "application/json"; who.Request.Body = new MemoryStream(body); who.Request.ContentLength = body.Length; who.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody()); }
            await Hosting.Errors(who, endpoint.RequestDelegate!);
            Assert.Equal(403, who.Response.StatusCode);
        }
        await using var db = Db(clinicA);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == clinicA);
        Assert.Equal(("reminders", true), (tenant.Mode, tenant.RemindersEnabled)); // nothing changed
    }
    // ======================= Ronda 3 (re-revisión) =======================

    // N1: BAJA has its own daily cap: the first BAJA of the day is always answered, even after the free-text line.
    [Fact]
    public async Task FreeTextThenBajaGetsTwoLines()
    {
        await Inbound(phone, text: "hola");
        await Inbound(phone, text: "BAJA");
        await Inbound(phone, text: "BAJA");

        Assert.Equal([FreeText, "Listo. Ya no recibirá recordatorios de citas desde este número."], kapso.Select(k => k.Body.GetProperty("text").GetProperty("body").GetString()));
    }

    // N2: a hospital selected only because a withdrawal is pending gets only the retry: no sync, no send.
    [Fact]
    public async Task AHospitalSelectedOnlyForAPendingWithdrawalGetsOnlyTheRetry()
    {
        // The own-number hospital, paused, with everything its own sync would need to read Hospital, and a general-number send awaiting withdrawal.
        await using (var db = own.NewContext())
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == own.Scope.Id); tenant.RemindersEnabled = false; tenant.ReminderChannelId = own.Conversation.ChannelId;
            var contact = await db.Contacts.SingleAsync(c => c.Id == own.Contact.Id); contact.PatientId = patientA; contact.ReminderConsentAt = DateTimeOffset.UtcNow;
            db.Add(new AppointmentReminder { TenantId = own.Scope.Id, PatientId = patientA, AppointmentId = appointmentA, StartsAt = start, DueAt = start.AddDays(-1), Window = "day_before", Status = "sent", AttemptedAt = DateTimeOffset.UtcNow, RecipientHash = Hash(phone), OptOutPendingAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await using (var root = own.NewDb(new TenantScope())) // the worker's root scope has no tenant: the selection must not depend on the filter
        {
            var (full, retryOnly) = await AppointmentReminderWorker.Pass(root, default);
            Assert.Contains(own.Scope.Id, retryOnly); Assert.DoesNotContain(own.Scope.Id, full);
            Assert.Contains(clinicA, full); Assert.DoesNotContain(clinicA, retryOnly);
        }

        await AppointmentReminderWorker.RetryTenant(Scopes(), own.Scope.Id, NullLogger.Instance, default);

        Assert.Equal($"/v1/patients/{patientA}/whatsapp-reminders/consent/withdraw", Assert.Single(hospitalWrites).Path);
        Assert.DoesNotContain(hospitalReads, path => path.StartsWith("/v1/patients/", StringComparison.Ordinal) || path.StartsWith("/v1/agenda/patients/", StringComparison.Ordinal));
    }

    // N3: a message whose processing failed is not taken as seen: Kapso's redelivery is processed.
    [Fact]
    public async Task ABajaThatFailedIsProcessedWhenRedelivered()
    {
        await Sent(clinicA, appointmentA, patientA); var id = "wamid." + Guid.NewGuid().ToString("N");
        hospitalThrowsOnce = true;
        await Assert.ThrowsAnyAsync<Exception>(() => Inbound(phone, text: "BAJA", messageId: id));

        await Inbound(phone, text: "BAJA", messageId: id);

        Assert.Equal($"/v1/patients/{patientA}/whatsapp-reminders/consent/withdraw", Assert.Single(hospitalWrites).Path);
        Assert.Single(kapso);
    }

    // N4: the withdrawal carries the moment of the BAJA; and is dropped when Hospital reports an authorisation recorded after it.
    [Fact]
    public async Task TheWithdrawalCarriesTheMomentOfTheBaja()
    {
        await Sent(clinicA, appointmentA, patientA);
        var before = DateTimeOffset.UtcNow;
        await Inbound(phone, text: "BAJA");
        var after = DateTimeOffset.UtcNow;

        using var body = JsonDocument.Parse(Assert.Single(hospitalWrites).Body);
        Assert.Equal("whatsapp-baja", body.RootElement.GetProperty("origin").GetString());
        var at = body.RootElement.GetProperty("optedOutAt").GetDateTimeOffset();
        Assert.InRange(at, before.AddMilliseconds(-1), after.AddMilliseconds(1));
    }
    // R2 (supersedes N4's read): T1 BAJA with Hospital down, T2 the desk authorises again, T3 the retry carries T1 and Hospital's own
    // precondition (criterio 33) keeps the newer authorisation. Recepción reads nothing before withdrawing.
    [Fact]
    public async Task AWithdrawalRetriedAfterANewAuthorisationCarriesTheBajaAndReadsNothing()
    {
        var sent = await Sent(clinicA, appointmentA, patientA);
        hospitalDown = true;
        var t1 = DateTimeOffset.UtcNow;
        await Inbound(phone, text: "BAJA"); // T1
        var pending = (await Row(clinicA, sent.Id)).OptOutPendingAt;
        Assert.NotNull(pending);
        hospitalDown = false; hospitalReads.Clear();
        recipients[clinicA] = [Recipient(Guid.NewGuid(), patientA, start.AddDays(1), consentSince: DateTimeOffset.UtcNow.AddMinutes(1))]; // T2

        await using (var db = Db(clinicA)) await Service(clinicA, db, Kapso()).RetryOptOuts(); // T3

        using var body = JsonDocument.Parse(Assert.Single(hospitalWrites).Body);
        Assert.Equal(pending!.Value, body.RootElement.GetProperty("optedOutAt").GetDateTimeOffset(), TimeSpan.FromMilliseconds(1));
        Assert.Empty(hospitalReads);
        Assert.Null((await Row(clinicA, sent.Id)).OptOutPendingAt);
    }

    // R2: a withdrawal Hospital refuses (4xx other than 408/429) ends: no more retries, a note on the row, and the owner sees it.
    [Theory][InlineData(HttpStatusCode.BadRequest)][InlineData(HttpStatusCode.NotFound)][InlineData(HttpStatusCode.Conflict)]
    public async Task AWithdrawalHospitalRefusesEndsAndIsShownToTheOwner(HttpStatusCode answer)
    {
        var sent = await Sent(clinicA, appointmentA, patientA); withdrawAnswer = answer;
        await Inbound(phone, text: "BAJA");
        await using (var db = Db(clinicA)) await Service(clinicA, db, Kapso()).RetryOptOuts();

        Assert.Single(hospitalWrites);
        var row = await Row(clinicA, sent.Id);
        Assert.Equal((null, null, AppointmentReminderService.WithdrawalRefused), (row.OptOutPendingAt, row.OptOutWithdrawnAt, row.Reason));
        var scope = new TenantScope(); await using var owner = own.NewDb(scope);
        Assert.Contains(await PlatformEndpoints.Doubts(Principal("\"platform-owner\""), owner, scope, default), d => d.Id == sent.Id && d.Reason == AppointmentReminderService.WithdrawalRefused);
    }
    // R2: 5xx, timeouts, 408 and 429 keep it pending and it is tried again.
    [Theory][InlineData(HttpStatusCode.InternalServerError)][InlineData(HttpStatusCode.ServiceUnavailable)][InlineData(HttpStatusCode.RequestTimeout)][InlineData(HttpStatusCode.TooManyRequests)]
    public async Task AWithdrawalThatFailsForNowIsRetried(HttpStatusCode answer)
    {
        var sent = await Sent(clinicA, appointmentA, patientA); withdrawAnswer = answer;
        await Inbound(phone, text: "BAJA");
        await using (var db = Db(clinicA)) await Service(clinicA, db, Kapso()).RetryOptOuts();
        Assert.Equal(2, hospitalWrites.Count);
        Assert.NotNull((await Row(clinicA, sent.Id)).OptOutPendingAt);

        withdrawAnswer = HttpStatusCode.OK;
        await using (var db = Db(clinicA)) await Service(clinicA, db, Kapso()).RetryOptOuts();
        Assert.Equal(3, hospitalWrites.Count);
        var row = await Row(clinicA, sent.Id);
        Assert.Null(row.OptOutPendingAt); Assert.NotNull(row.OptOutWithdrawnAt);
    }

    // R3: the withdrawal carries the last BAJA, not the first.
    [Fact]
    public async Task BajaHospitalDownReauthorisationBajaIsOneWithdrawalWithTheLastBaja()
    {
        await Sent(clinicA, appointmentA, patientA);
        hospitalDown = true;
        await Inbound(phone, text: "BAJA");
        hospitalDown = false; // meanwhile the desk authorised again in Hospital
        await Task.Delay(20);
        var second = DateTimeOffset.UtcNow;
        await Inbound(phone, text: "BAJA");

        using var body = JsonDocument.Parse(Assert.Single(hospitalWrites).Body);
        Assert.True(body.RootElement.GetProperty("optedOutAt").GetDateTimeOffset() >= second.AddMilliseconds(-1));
    }

    // Nit: the platform's number never becomes a clinic's channel, whichever way a channel is created.
    [Fact]
    public async Task CreatingAChannelWithThePlatformNumberIsRefused()
    {
        using var services = Api.Value.Services.CreateScope();
        services.ServiceProvider.GetRequiredService<CurrentUser>().Role = "admin"; services.ServiceProvider.GetRequiredService<TenantScope>().Id = own.Scope.Id;
        var ctx = new DefaultHttpContext { RequestServices = services.ServiceProvider }; ctx.Request.Method = "POST"; ctx.Response.Body = new MemoryStream();
        var body = JsonSerializer.SerializeToUtf8Bytes(new { name = "Clínica", phoneNumberId = PlatformNumber, coexistence = false });
        ctx.Request.ContentType = "application/json"; ctx.Request.Body = new MemoryStream(body); ctx.Request.ContentLength = body.Length; ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody());

        await Hosting.Errors(ctx, Route("POST", "/api/channels").RequestDelegate!);

        Assert.Equal(400, ctx.Response.StatusCode);
        ctx.Response.Body.Position = 0;
        Assert.Contains("número general de la plataforma", new StreamReader(ctx.Response.Body).ReadToEnd());
    }


    // N6: a send whose delivery is unknown may have reached the phone: a BAJA withdraws for it too.
    [Fact]
    public async Task ABajaAlsoWithdrawsForAnUncertainSend()
    {
        await Sent(clinicA, appointmentA, patientA, "uncertain");
        await Inbound(phone, text: "BAJA");
        Assert.Equal((clinicA, $"/v1/patients/{patientA}/whatsapp-reminders/consent/withdraw"), (Assert.Single(hospitalWrites).Tenant, Assert.Single(hospitalWrites).Path));
    }

    // N7: the doubts are the last week's, newest first, and every read leaves the operator's audit entry in each hospital shown.
    [Fact]
    public async Task TheDoubtsAreRecentNewestFirstAndAudited()
    {
        var older = await Sent(clinicA, appointmentA, patientA, "uncertain");
        var newer = await Sent(clinicB, appointmentB, patientB, "cancelling", start.AddDays(1));
        var stale = await Sent(clinicA, Guid.NewGuid(), patientA, "uncertain", DateTimeOffset.UtcNow.AddDays(-8));
        var scope = new TenantScope(); await using var db = own.NewDb(scope);

        var doubts = (await PlatformEndpoints.Doubts(Principal("\"platform-owner\""), db, scope, default)).Where(d => d.TenantId == clinicA || d.TenantId == clinicB).Select(d => d.Id).ToList();

        Assert.Equal([newer.Id, older.Id], doubts);
        await using var read = own.NewDb(new TenantScope());
        var audits = await read.Audits.IgnoreQueryFilters().Where(a => a.Action == "reminder_doubts.read" && (a.TenantId == clinicA || a.TenantId == clinicB)).ToListAsync();
        Assert.Equal(new[] { clinicA, clinicB }.Order(), audits.Select(a => a.TenantId).Order());
        Assert.All(audits, a => Assert.Equal("platform:" + sub, a.Actor));
    }

    // Nit: the platform's number is never a clinic's channel; the start fails if it is.
    [Fact]
    public async Task TheStartRefusesAPlatformNumberThatIsAClinicsChannel()
    {
        await using var db = own.NewDb(new TenantScope());
        var clinicNumber = (await db.Channels.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == own.Conversation.ChannelId)).PhoneNumberId;
        await Assert.ThrowsAsync<InvalidOperationException>(() => GeneralNumber.RequireNotAClinicNumber(db, Config(new() { ["KAPSO_PLATFORM_PHONE_NUMBER_ID"] = clinicNumber })));
        await GeneralNumber.RequireNotAClinicNumber(db, Config());
    }

    // ======================= Ronda 5 =======================

    // Q1: a refused withdrawal is shown by the BAJA's date, not the appointment's: an appointment of last month still appears.
    [Fact]
    public async Task ARefusedWithdrawalOfAnOldAppointmentStillShows()
    {
        var old = await Sent(clinicA, appointmentA, patientA, at: DateTimeOffset.UtcNow.AddDays(-10)); withdrawAnswer = HttpStatusCode.Conflict;
        await Inbound(phone, text: "BAJA");

        var scope = new TenantScope(); await using var owner = own.NewDb(scope);
        Assert.Contains(await PlatformEndpoints.Doubts(Principal("\"platform-owner\""), owner, scope, default), d => d.Id == old.Id);
    }
    // Nit: the refusal note is added to what the row already said; «Hospital no confirmó…» of a cancellation in doubt is kept.
    [Fact]
    public async Task ARefusedWithdrawalKeepsTheCancellationNote()
    {
        var row = await Sent(clinicA, appointmentA, patientA, "cancelling"); withdrawAnswer = HttpStatusCode.Conflict;
        const string note = "El paciente pidió cancelar desde el recordatorio del número general; Hospital no confirmó. Verifique la agenda.";
        await using (var db = Db(clinicA)) await db.AppointmentReminders.Where(r => r.Id == row.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.Reason, note));

        await Inbound(phone, text: "BAJA");

        var reason = (await Row(clinicA, row.Id)).Reason!;
        Assert.Contains(note, reason); Assert.Contains(AppointmentReminderService.WithdrawalRefused, reason);
        var scope = new TenantScope(); await using var owner = own.NewDb(scope);
        Assert.Single(await PlatformEndpoints.Doubts(Principal("\"platform-owner\""), owner, scope, default), d => d.Id == row.Id);
    }

    /// <summary>What Kestrel tells minimal APIs about a request with a body; a bare DefaultHttpContext says nothing and the body is never bound.</summary>
    sealed class HasBody : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
