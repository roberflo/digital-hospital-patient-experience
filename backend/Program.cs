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
builder.Services.AddRecepcionServices(config, dev);
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
        // The platform owner never goes through Bind: no Member, no Tenant, no first-sign-in sync (docs/platform-owner.md).
        var gate = await PlatformOwner.Gate(ctx, ctx.RequestServices.GetRequiredService<CrmDb>(), ctx.RequestServices.GetRequiredService<TenantScope>(), ctx.RequestServices.GetRequiredService<CurrentUser>());
        if (gate is > 0) { ctx.Response.StatusCode = gate.Value; return; }
        if (gate is null && !await Identity.Bind(ctx, ctx.RequestServices.GetRequiredService<CrmDb>(), ctx.RequestServices.GetRequiredService<TenantScope>(), ctx.RequestServices.GetRequiredService<CurrentUser>(), config))
        {
            ctx.Response.StatusCode = 403;
            if (ctx.Items.ContainsKey("hospital-pending")) await ctx.Response.WriteAsJsonAsync(new { title = "Tu hospital aún no está dado de alta en Recepción. Pide al Administrador del hospital que inicie sesión primero.", code = "hospital_not_onboarded" });
            return;
        }
        // First sign-in of a hospital: take its real name and zone from Hospital, best effort.
        if (ctx.Items.ContainsKey("tenant-created")) { var sp = ctx.RequestServices; await HospitalIdentitySync.Run(sp.GetRequiredService<CrmDb>(), sp.GetRequiredService<TenantScope>(), sp.GetRequiredService<CurrentUser>(), sp.GetRequiredService<HospitalClient>(), sp.GetRequiredService<ILogger<HospitalClient>>(), ctx.RequestAborted, TimeSpan.FromSeconds(3)); }
    }
    await next();
});
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (CrmDb db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
app.UseRateLimiter();
app.MapRecepcionApi();
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
