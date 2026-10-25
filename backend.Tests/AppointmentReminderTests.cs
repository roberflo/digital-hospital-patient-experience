using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Recepcion;
using Recepcion.Integrations;
using Xunit;

/// <summary>MR-13: the worker's own per-tenant pass. A contact whose Hospital read hangs is one failure, not the
/// end of the sync, and a reminder already due is sent even when the sync spends its whole budget.</summary>
public sealed class AppointmentReminderTests : IAsyncLifetime
{
    readonly Guid tenant = Guid.NewGuid();
    readonly Guid hanging = Guid.NewGuid(), answering = Guid.NewGuid(), appointment = Guid.NewGuid();
    readonly DateTimeOffset start = ReminderRules.Instant(DateTimeOffset.UtcNow.AddMinutes(50));
    DbContextOptions<CrmDb> options = null!; readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
    Contact first = null!, second = null!; int sends; readonly System.Collections.Concurrent.ConcurrentQueue<Guid> reads = new(); HttpStatusCode delivery = HttpStatusCode.OK; readonly System.Collections.Concurrent.ConcurrentQueue<string> sentTo = new();

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_DATABASE");
        if (string.IsNullOrEmpty(connection)) throw new InvalidOperationException("Run scripts/test-backend.sh to provide the isolated PostgreSQL database.");
        options = new DbContextOptionsBuilder<CrmDb>().UseNpgsql(connection).Options;
        await using var db = new CrmDb(options, new TenantScope { Id = tenant }, protection); await db.Database.MigrateAsync();
        var channel = new Channel { TenantId = tenant, Name = "Synthetic", PhoneNumberId = Guid.NewGuid().ToString("N"), Enabled = true }; db.Add(channel);
        db.Tenants.Add(new Tenant { Id = tenant, Name = "Hospital Sintético", RemindersEnabled = true, ReminderChannelId = channel.Id });
        // Inserted against their Id order, so a walk in storage order is told apart from one by Id; the tests below hold for either order.
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() }.OrderByDescending(id => id.ToString()).ToArray();
        db.Add(first = new Contact { Id = ids[0], TenantId = tenant, Name = "Synthetic one", Phone = "50370000001", PhoneHash = Guid.NewGuid().ToString(), PatientId = hanging, ReminderConsentAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        second = new Contact { Id = ids[1], TenantId = tenant, Name = "Synthetic two", Phone = "50370000002", PhoneHash = Guid.NewGuid().ToString(), PatientId = answering, ReminderConsentAt = DateTimeOffset.UtcNow };
        db.Add(second); await db.SaveChangesAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AContactThatHangsIsAFailureAndTheSyncGoesOnWithTheOthers()
    {
        await AppointmentReminderWorker.RunTenant(Services(), tenant, NullLogger.Instance, CancellationToken.None, contactBudget: TimeSpan.FromSeconds(3));
        await using var db = Db();
        Assert.Equal(2, await db.AppointmentReminders.CountAsync(r => r.ContactId == second.Id));
        Assert.Contains("1 contacto", (await db.Tenants.SingleAsync(t => t.Id == tenant)).ReminderSyncError);
        // The walk has a stable order (by contact Id), not whatever order the database returns.
        Assert.Equal(new[] { first, second }.OrderBy(c => c.Id.ToString()).Select(c => c.PatientId!.Value), reads.Take(2));
    }

    [Fact]
    public async Task ADueReminderIsSentEvenWhenTheSyncSpendsItsBudget()
    {
        await using (var db = Db())
        {
            db.Add(new AppointmentReminder { TenantId = tenant, ContactId = second.Id, PatientId = answering, ChannelId = (await db.Tenants.SingleAsync(t => t.Id == tenant)).ReminderChannelId!.Value, AppointmentId = appointment, StartsAt = start, DueAt = start.AddHours(-1), Window = "hour_before" });
            await db.SaveChangesAsync();
        }
        await AppointmentReminderWorker.RunTenant(Services(), tenant, NullLogger.Instance, CancellationToken.None, syncBudget: TimeSpan.FromMilliseconds(500));
        await using var check = Db();
        Assert.Equal(1, sends);
        Assert.Equal("sent", (await check.AppointmentReminders.SingleAsync(r => r.Window == "hour_before" && r.AppointmentId == appointment)).Status);
    }

    // Criterio 27/28: a contact stored with 8 digits before 503 was assumed gets its own-number reminder at «+503…», read, never rewritten.
    [Fact]
    public async Task AnOldEightDigitContactGetsItsReminderWithTheCountryCode()
    {
        await using (var db = Db())
        {
            (await db.Contacts.SingleAsync(c => c.Id == second.Id)).Phone = "70000001"; second.Phone = "70000001";
            db.Add(new AppointmentReminder { TenantId = tenant, ContactId = second.Id, PatientId = answering, ChannelId = (await db.Tenants.SingleAsync(t => t.Id == tenant)).ReminderChannelId!.Value, AppointmentId = appointment, StartsAt = start, DueAt = start.AddHours(-1), Window = "hour_before" });
            await db.SaveChangesAsync();
        }
        await AppointmentReminderWorker.RunTenant(Services(), tenant, NullLogger.Instance, CancellationToken.None, contactBudget: TimeSpan.FromSeconds(3));
        Assert.Equal(["+50370000001"], sentTo);
        await using var check = Db();
        Assert.Equal("70000001", (await check.Contacts.SingleAsync(c => c.Id == second.Id)).Phone);
    }

    // Kapso refusing the send (4xx) is a definite failure; a 5xx may have been delivered and stays uncertain. Neither is retried.
    [Theory][InlineData(HttpStatusCode.BadRequest, "failed")][InlineData(HttpStatusCode.ServiceUnavailable, "uncertain")][InlineData(HttpStatusCode.Accepted, "uncertain")] // 2xx without a message id: it may have been sent
    public async Task AReminderKapsoRefusesIsFailedAndOneItMayHaveSentIsUncertain(HttpStatusCode status, string expected)
    {
        delivery = status;
        await using (var db = Db())
        {
            db.Add(new AppointmentReminder { TenantId = tenant, ContactId = second.Id, PatientId = answering, ChannelId = (await db.Tenants.SingleAsync(t => t.Id == tenant)).ReminderChannelId!.Value, AppointmentId = appointment, StartsAt = start, DueAt = start.AddHours(-1), Window = "hour_before" });
            await db.SaveChangesAsync();
        }
        var services = Services();
        await AppointmentReminderWorker.RunTenant(services, tenant, NullLogger.Instance, CancellationToken.None, contactBudget: TimeSpan.FromSeconds(3));
        await AppointmentReminderWorker.RunTenant(services, tenant, NullLogger.Instance, CancellationToken.None, contactBudget: TimeSpan.FromSeconds(3));
        await using var check = Db();
        var row = await check.AppointmentReminders.SingleAsync(r => r.Window == "hour_before" && r.AppointmentId == appointment);
        Assert.Equal(expected, row.Status);
        Assert.Equal(expected, (await check.Messages.SingleAsync(m => m.Id == row.MessageId)).Status);
        Assert.Equal(expected == "failed" ? "WhatsApp rechazó el envío; no se entregó." : "Entrega sin confirmar. Revisa WhatsApp antes de volver a enviar.", row.Reason);
        Assert.Equal(1, sends);
    }

    CrmDb Db() => new(options, new TenantScope { Id = tenant }, protection);

    IServiceScopeFactory Services()
    {
        var token = "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { tenant_id = tenant, exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic";
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Hospital:Tenants:{tenant}:BaseUrl"] = "https://hospital.example.invalid/", [$"Hospital:Tenants:{tenant}:AccessToken"] = token,
            ["REMINDERS_SEND_ENABLED"] = "true", ["KAPSO_API_KEY"] = "synthetic"
        }).Build();
        var hospital = new Handler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/v1/patients/")) reads.Enqueue(Guid.Parse(path["/v1/patients/".Length..]));
            if (path.Contains(hanging.ToString())) { await Task.Delay(Timeout.Infinite, ct); }
            if (path == $"/v1/patients/{answering}") return Json(new { patientId = answering, phone = second.Phone });
            if (path == $"/v1/agenda/patients/{answering}") return Json(new { rows = new[] { new { appointmentId = appointment, scheduledStart = start, status = "booked" } } });
            throw new InvalidOperationException("Unexpected Hospital route");
        });
        var kapso = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(request.RequestUri!.AbsolutePath.Contains("message_templates")
                ? Json(new { data = new[] { new { name = "recepcion_cita_una_hora_v1", language = "es", status = "APPROVED", category = "UTILITY", parameter_format = "NAMED", components = new[] { new { type = "BODY", text = "Cita en {{hospital}} el {{fecha}} a las {{hora}}. BAJA" } } } } })
                : Json(new { data = new { business_account_id = "synthetic-waba" } }));
            Interlocked.Increment(ref sends);
            var to = JsonDocument.Parse(request.Content!.ReadAsStringAsync(CancellationToken.None).Result).RootElement.GetProperty("to").GetString()!;
            sentTo.Enqueue(to);
            if (delivery != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(delivery) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
            return Task.FromResult(AgentHarness.KapsoSent("reminder-" + Guid.NewGuid(), to.TrimStart('+')));
        });
        var services = new ServiceCollection();
        services.AddSingleton(config); services.AddSingleton(protection); services.AddScoped<TenantScope>();
        services.AddScoped(sp => new CrmDb(options, sp.GetRequiredService<TenantScope>(), protection));
        services.AddScoped(_ => new HospitalClient(new HttpClient(hospital), config));
        services.AddScoped(_ => new KapsoClient(new HttpClient(kapso), config));
        services.AddScoped<ConversationService>(); services.AddScoped<AppointmentReminderService>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
    static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
