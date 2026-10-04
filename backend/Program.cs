using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Recepcion.Integrations;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
// Development only relaxes transport (http issuer on the Docker network) and seeds synthetic data.
// It never adds a second issuer: every token, in every environment, is Hospital's Keycloak's.
var dev = builder.Environment.IsDevelopment();
if (string.IsNullOrWhiteSpace(config["PHONE_HASH_KEY"]) || config["PHONE_HASH_KEY"]!.Length < 32) throw new InvalidOperationException("PHONE_HASH_KEY must be at least 32 characters");
if (string.IsNullOrWhiteSpace(config["Auth:Authority"])) throw new InvalidOperationException("Keycloak Authority required");
builder.Services.AddScoped<TenantScope>(); builder.Services.AddScoped<CurrentUser>();
builder.Services.AddDbContext<CrmDb>(o => o.UseNpgsql(config.GetConnectionString("Database")));
builder.Services.AddDataProtection().SetApplicationName("Recepcion").PersistKeysToFileSystem(new DirectoryInfo(config["KEY_DIRECTORY"] ?? "/keys"));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => HospitalIdentity.Configure(o, config, dev));
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("assistant", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.User.FindFirst("sub")?.Value ?? "anonymous", _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddHttpClient<KapsoClient>(c => c.Timeout = TimeSpan.FromSeconds(30)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<HospitalClient>(c => { c.Timeout = TimeSpan.FromSeconds(30); c.MaxResponseContentBufferSize = 20 * 1024 * 1024; }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).RemoveAllLoggers();
builder.Services.AddHttpClient<HospitalClinicalClient>(c => { c.Timeout = TimeSpan.FromSeconds(30); c.MaxResponseContentBufferSize = 8 * 1024 * 1024; }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<AgentRuntime>(c => c.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddHttpClient<GoogleCalendarClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<AppointmentReminderService>();
builder.Services.AddScoped<CommercialService>();
builder.Services.AddScoped<HospitalConnectionStore>();
builder.Services.AddHttpClient("hospital-setup", c=>{c.Timeout=TimeSpan.FromSeconds(30);c.MaxResponseContentBufferSize=20*1024*1024;}).ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false});
builder.Services.AddHostedService<CommercialSyncWorker>();
builder.Services.AddHostedService<AppointmentReminderWorker>();
builder.Services.AddScoped<WhatsAppOnboarding>();
builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddHostedService<CalendarWorker>();
builder.Services.AddProblemDetails();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 20 * 1024 * 1024);
var app = builder.Build();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Cache-Control"] = "no-store";
    try { await next(); }
    catch (AccessDeniedException) { await Results.Problem(statusCode: 403, title: "No tienes permiso para esta operación").ExecuteAsync(ctx); }
    catch (ArgumentException ex) { await Results.Problem(statusCode: 400, title: ex.Message).ExecuteAsync(ctx); }
    catch (DbUpdateConcurrencyException) { await Results.Problem(statusCode: 409, title: "La conversación cambió. Actualiza e inténtalo de nuevo.").ExecuteAsync(ctx); }
    catch (DbUpdateException) { await Results.Problem(statusCode: 409, title: "El registro ya existe o tiene referencias incompatibles").ExecuteAsync(ctx); }
    catch (HospitalIntegrationException ex) { await Results.Problem(statusCode: (int)ex.StatusCode is 400 or 403 or 404 or 409 or 503 ? (int)ex.StatusCode : 502, title: ex.DisplayMessage, extensions: new Dictionary<string,object?>{{"code",ex.Code}}).ExecuteAsync(ctx); }
    catch (HttpRequestException) { await Results.Problem(statusCode: 502, title: "No se pudo completar la conexión externa. Revisa el historial antes de reintentar.").ExecuteAsync(ctx); }
    catch (Exception ex) { app.Logger.LogError("Request failed {Type} {Trace}", ex.GetType().Name, ctx.TraceIdentifier); await Results.Problem(statusCode: 500, title: "No se pudo completar la operación", extensions: new Dictionary<string, object?> { { "traceId", ctx.TraceIdentifier } }).ExecuteAsync(ctx); }
});
app.UseAuthentication(); app.UseAuthorization();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        if (ctx.User.Identity?.IsAuthenticated != true) { ctx.Response.StatusCode = 401; return; }
        if (!await Identity.Bind(ctx, ctx.RequestServices.GetRequiredService<CrmDb>(), ctx.RequestServices.GetRequiredService<TenantScope>(), ctx.RequestServices.GetRequiredService<CurrentUser>(), config)) { ctx.Response.StatusCode = 403; return; }
        // First sign-in of a hospital: take its real name and zone from Hospital, best effort.
        if (ctx.Items.ContainsKey("tenant-created")) { var sp = ctx.RequestServices; await HospitalIdentitySync.Run(sp.GetRequiredService<CrmDb>(), sp.GetRequiredService<TenantScope>(), sp.GetRequiredService<CurrentUser>(), sp.GetRequiredService<HospitalClient>(), sp.GetRequiredService<ILogger<HospitalClient>>(), ctx.RequestAborted, TimeSpan.FromSeconds(3)); }
    }
    await next();
});
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (CrmDb db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
app.UseRateLimiter();
app.MapHospitalConnection();app.MapCommercial();app.MapReminderEndpoints();app.MapActivityFeed();app.MapClinical();app.MapTeam();app.MapProductivity();app.MapChannelDiagnostics();app.MapInbox();app.MapCrm(); app.MapWhatsApp(); app.MapHospital(); app.MapGoogle(); app.MapAssistant();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CrmDb>();
    if (dev || config["INITIALIZE_DATABASE"] == "true") { await db.Database.MigrateAsync(); }
    if (dev) {
        await DemoSeed.Run(db, scope.ServiceProvider.GetRequiredService<TenantScope>());
        if(Guid.TryParse(config["DEV_HOSPITAL_TENANT_ID"],out var hospitalTenant)&&hospitalTenant!=Guid.Empty&&!await db.Tenants.AnyAsync(x=>x.Id==hospitalTenant)){
            db.Tenants.Add(new Tenant{Id=hospitalTenant,Name="Hospital local · clínica sintética",AgentEnabled=false});await db.SaveChangesAsync();
        }
    }
    else if (Guid.TryParse(config["BOOTSTRAP_TENANT_ID"], out var tid) && !await db.Tenants.AnyAsync(x => x.Id == tid)) { db.Tenants.Add(new Tenant { Id = tid, Name = config["BOOTSTRAP_TENANT_NAME"] ?? "Hospital" }); await db.SaveChangesAsync(); }
}
await app.RunAsync();
public partial class Program;
