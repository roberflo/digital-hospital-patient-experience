using Microsoft.EntityFrameworkCore;
using Recepcion;
using Recepcion.Integrations;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
// Development only relaxes transport (http issuer on the Docker network) and seeds synthetic data.
// It never adds a second issuer: every token, in every environment, is Hospital's Keycloak's.
var dev = builder.Environment.IsDevelopment();
if (string.IsNullOrWhiteSpace(config["PHONE_HASH_KEY"]) || config["PHONE_HASH_KEY"]!.Length < 32) throw new InvalidOperationException("PHONE_HASH_KEY must be at least 32 characters");
GeneralNumber.RequireSeparateCredentials(config);
if (string.IsNullOrWhiteSpace(config["Auth:Authority"])) throw new InvalidOperationException("Keycloak Authority required");
Hosting.RequireKeyringCertificate(config, builder.Environment);
builder.Services.AddRecepcionServices(config, dev);
builder.Services.AddRecepcionTelemetry(config, builder.Environment);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 20 * 1024 * 1024);
var app = builder.Build();
Hosting.ProtectKeyring(app.Services, config);
app.Use(Hosting.Errors);
app.UseAuthentication(); app.UseAuthorization();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        if (ctx.User.Identity?.IsAuthenticated != true) { ctx.Response.StatusCode = 401; return; }
        // The platform owner never goes through Bind: no Member, no Tenant, no first-sign-in sync (docs/platform-owner.md).
        var gate = await PlatformOwner.Gate(ctx, ctx.RequestServices.GetRequiredService<CrmDb>(), ctx.RequestServices.GetRequiredService<TenantScope>(), ctx.RequestServices.GetRequiredService<CurrentUser>());
        if (gate is > 0) { ctx.Response.StatusCode = gate.Value; return; }
        if (gate is null && !await Identity.Bind(ctx, ctx.RequestServices.GetRequiredService<CrmDb>(), ctx.RequestServices.GetRequiredService<TenantScope>(), ctx.RequestServices.GetRequiredService<CurrentUser>(), config))
        {
            ctx.Response.StatusCode = 403;
            if (ctx.Items.ContainsKey("hospital-reminders-only")) await ctx.Response.WriteAsJsonAsync(new { title = "Tu hospital no usa Recepción: sólo recibe recordatorios de citas desde el número general de la plataforma.", code = "hospital_reminders_only" });
            else if (ctx.Items.ContainsKey("hospital-pending")) await ctx.Response.WriteAsJsonAsync(new { title = "Tu hospital aún no está dado de alta en Recepción. Pide al Administrador del hospital que inicie sesión primero.", code = "hospital_not_onboarded" });
            return;
        }
        // First sign-in of a hospital: take its real name and zone from Hospital, best effort.
        if (ctx.Items.ContainsKey("tenant-created")) { var sp = ctx.RequestServices; await HospitalIdentitySync.Run(sp.GetRequiredService<CrmDb>(), sp.GetRequiredService<TenantScope>(), sp.GetRequiredService<CurrentUser>(), sp.GetRequiredService<HospitalClient>(), sp.GetRequiredService<ILogger<HospitalClient>>(), ctx.RequestAborted, TimeSpan.FromSeconds(3)); }
    }
    await next();
});
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
// A deploy that forgot INITIALIZE_DATABASE is not ready: every turn would fail on a missing table (docs/deployment.md).
app.MapGet("/health/ready", async (CrmDb db) => await db.Database.CanConnectAsync() && !(await db.Database.GetPendingMigrationsAsync()).Any() ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
// What an external monitor polls to wake a person (docs/deployment.md): 503 while, in any hospital, an agent turn failed or was abandoned,
// or a send or a reminder became uncertain, in the last 30 minutes. Counts only: nothing names a hospital or a patient.
app.MapGet("/health/attention", async (CrmDb db) =>
{
    var since = DateTimeOffset.UtcNow.AddMinutes(-30);
    var turns = await db.Jobs.IgnoreQueryFilters().CountAsync(x => (x.Status == "failed" || x.Status == "uncertain") && (x.StartedAt ?? x.CreatedAt) > since);
    var sends = await db.Messages.IgnoreQueryFilters().CountAsync(x => x.Status == "uncertain" && x.Sender != "patient" && x.ReceivedAt > since);
    var reminders = await db.AppointmentReminders.IgnoreQueryFilters().CountAsync(x => x.Status == "uncertain" && x.AttemptedAt > since);
    var body = new { status = turns + sends + reminders == 0 ? "ok" : "attention", turns, sends, reminders };
    return turns + sends + reminders == 0 ? Results.Ok(body) : Results.Json(body, statusCode: 503);
}).RequireRateLimiting("health");
app.UseRateLimiter();
app.MapRecepcionApi();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CrmDb>();
    if (dev || config["INITIALIZE_DATABASE"] == "true") { await db.Database.MigrateAsync(); }
    await GeneralNumber.RequireNotAClinicNumber(db, config);
    if (dev) {
        await DemoSeed.Run(db, scope.ServiceProvider.GetRequiredService<TenantScope>());
        if(Guid.TryParse(config["DEV_HOSPITAL_TENANT_ID"],out var hospitalTenant)&&hospitalTenant!=Guid.Empty&&!await db.Tenants.AnyAsync(x=>x.Id==hospitalTenant)){
            db.Tenants.Add(new Tenant{Id=hospitalTenant,Name="Hospital local · clínica sintética",AgentEnabled=false});await db.SaveChangesAsync();
        }
        await DemoSeed.Specialties(db, scope.ServiceProvider.GetRequiredService<TenantScope>(), hospitalTenant == Guid.Empty ? null : hospitalTenant);
    }
    else if (Guid.TryParse(config["BOOTSTRAP_TENANT_ID"], out var tid) && !await db.Tenants.AnyAsync(x => x.Id == tid)) { db.Tenants.Add(new Tenant { Id = tid, Name = config["BOOTSTRAP_TENANT_NAME"] ?? "Hospital" }); await db.SaveChangesAsync(); }
}
await app.RunAsync();
public partial class Program;
