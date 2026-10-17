using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
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
        var keyring = services.AddDataProtection().SetApplicationName("Recepcion").PersistKeysToFileSystem(new DirectoryInfo(KeyDirectory(config)));
        if (KeyringCertificate(config) is { } cert) keyring.ProtectKeysWithCertificate(cert);
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
    // MR-37: the keyring certificate lives outside /keys and outside every backup (docs/deployment.md).
    public static X509Certificate2? KeyringCertificate(IConfiguration config)
    {
        var path = config["DATAPROTECTION_CERT_PATH"];
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!File.Exists(path)) throw new InvalidOperationException("DATAPROTECTION_CERT_PATH does not point to a readable PKCS#12 file");
        X509Certificate2 cert;
        try { cert = X509CertificateLoader.LoadPkcs12FromFile(path, config["DATAPROTECTION_CERT_PASSWORD"]); }
        catch (CryptographicException) { throw new InvalidOperationException("DATAPROTECTION_CERT_PATH could not be opened: wrong DATAPROTECTION_CERT_PASSWORD or not a PKCS#12 file"); }
        return cert.HasPrivateKey ? cert : throw new InvalidOperationException("DATAPROTECTION_CERT_PATH has no private key: the keyring could not be read back");
    }
    public static void RequireKeyringCertificate(IConfiguration config, IHostEnvironment env)
    {
        if (env.IsProduction() && string.IsNullOrWhiteSpace(config["DATAPROTECTION_CERT_PATH"]))
            throw new InvalidOperationException("DATAPROTECTION_CERT_PATH is required in Production: mount the keyring certificate outside /keys (docs/deployment.md)");
    }
    /// <summary>Once at startup: with a certificate and no encrypted key yet, a new key becomes the default so new data stops depending on plaintext keys. Old keys stay readable.</summary>
    public static void ProtectKeyring(IServiceProvider services, IConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config["DATAPROTECTION_CERT_PATH"])) return;
        var dir = new DirectoryInfo(KeyDirectory(config));
        // ponytail: scans the key files; the keyring is a handful of small XML files.
        if (dir.Exists && dir.EnumerateFiles("*.xml").Any(f => File.ReadAllText(f.FullName).Contains("<encryptedSecret", StringComparison.Ordinal))) return;
        var now = DateTimeOffset.UtcNow;
        services.GetRequiredService<IKeyManager>().CreateNewKey(now, now.AddDays(90));
    }
    static string KeyDirectory(IConfiguration config) => config["KEY_DIRECTORY"] ?? "/keys";
    /// <summary>The outermost middleware Program.cs installs: security headers and the one place an exception becomes a response.</summary>
    public static async Task Errors(HttpContext ctx, RequestDelegate next)
    {
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        try { await next(ctx); }
        catch (AccessDeniedException) { await Results.Problem(statusCode: 403, title: "No tienes permiso para esta operación").ExecuteAsync(ctx); }
        catch (ArgumentException ex) { await Results.Problem(statusCode: 400, title: ex.Message).ExecuteAsync(ctx); }
        catch (DbUpdateConcurrencyException) { await Results.Problem(statusCode: 409, title: "La conversación cambió. Actualiza e inténtalo de nuevo.").ExecuteAsync(ctx); }
        catch (DbUpdateException) { await Results.Problem(statusCode: 409, title: "El registro ya existe o tiene referencias incompatibles").ExecuteAsync(ctx); }
        catch (HospitalIntegrationException ex) { Log(ctx).LogWarning("Hospital integration failed {Code} {Trace}", ex.Code, ctx.TraceIdentifier); await Results.Problem(statusCode: (int)ex.StatusCode is 400 or 403 or 404 or 409 or 503 or 504 ? (int)ex.StatusCode : 502, title: ex.DisplayMessage, extensions: new Dictionary<string,object?>{{"code",ex.Code}}).ExecuteAsync(ctx); }
        catch (HttpRequestException) { await Results.Problem(statusCode: 502, title: "No se pudo completar la conexión externa. Revisa el historial antes de reintentar.").ExecuteAsync(ctx); }
        catch (Exception ex) { Log(ctx).LogError("Request failed {Type} {Trace}", ex.GetType().Name, ctx.TraceIdentifier); await Results.Problem(statusCode: 500, title: "No se pudo completar la operación", extensions: new Dictionary<string, object?> { { "traceId", ctx.TraceIdentifier } }).ExecuteAsync(ctx); }
    }
    static ILogger Log(HttpContext ctx) => ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Recepcion.Api");
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
