using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

// Program.cs's registrations and route table, callable without starting the host: the platform-owner
// sweep (backend.Tests/PlatformOwnerTests.cs) builds the app and enumerates every mapped endpoint.
public static class Hosting
{
    public static void AddRecepcionServices(this IServiceCollection services, IConfiguration config, bool dev)
    {
        services.AddScoped<TenantScope>(); services.AddScoped<CurrentUser>();
        services.AddDbContext<CrmDb>(o => o.UseNpgsql(config.GetConnectionString("Database")));
        services.AddDataProtection().SetApplicationName("Recepcion").PersistKeysToFileSystem(new DirectoryInfo(config["KEY_DIRECTORY"] ?? "/keys"));
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => HospitalIdentity.Configure(o, config, dev));
        services.AddAuthorization();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.AddPolicy("assistant", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.User.FindFirst("sub")?.Value ?? "anonymous", _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(1) }));
        });
        services.AddHttpClient<KapsoClient>(c => c.Timeout = TimeSpan.FromSeconds(30)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<HospitalClient>(c => { c.Timeout = TimeSpan.FromSeconds(30); c.MaxResponseContentBufferSize = 20 * 1024 * 1024; }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).RemoveAllLoggers();
        services.AddHttpClient<HospitalClinicalClient>(c => { c.Timeout = TimeSpan.FromSeconds(30); c.MaxResponseContentBufferSize = 8 * 1024 * 1024; }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<AgentRuntime>(c => c.Timeout = TimeSpan.FromSeconds(90));
        services.AddHttpClient<GoogleCalendarClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<ConversationService>();
        services.AddScoped<AppointmentReminderService>();
        services.AddScoped<CommercialService>();
        services.AddScoped<HospitalConnectionStore>();
        services.AddHttpClient("hospital-setup", c=>{c.Timeout=TimeSpan.FromSeconds(30);c.MaxResponseContentBufferSize=20*1024*1024;}).ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false});
        services.AddHostedService<CommercialSyncWorker>();
        services.AddHostedService<AppointmentReminderWorker>();
        services.AddScoped<WhatsAppOnboarding>();
        services.AddHostedService<AgentWorker>();
        services.AddHostedService<CalendarWorker>();
        services.AddProblemDetails();
    }
    public static void MapRecepcionApi(this WebApplication app)
    {
        app.MapPlatformOwner();
        app.MapHospitalConnection();
        app.MapCommercial();
        app.MapReminderEndpoints();
        app.MapActivityFeed();
        app.MapClinical();
        app.MapTeam();
        app.MapProductivity();
        app.MapChannelDiagnostics();
        app.MapInstallation();
        app.MapInbox();
        app.MapCrm();
        app.MapWhatsApp();
        app.MapHospital();
        app.MapGoogle();
        app.MapAssistant();
    }
}
