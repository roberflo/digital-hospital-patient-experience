using Microsoft.EntityFrameworkCore;
namespace Recepcion;

public record ReminderClinicInput(bool Enabled);
public record GeneralDoubt(Guid Id, Guid TenantId, DateTimeOffset StartsAt, string Status, string? Reason);
/// <summary>recordatorios-whatsapp.spec.md criterio 17 and A16: a hospital of the realm without Recepción is put on the platform's general number
/// only by an explicit, audited act of the platform operator (realm role <c>platform-owner</c>, no <c>tenant_id</c>). Outside <c>/api</c>: it is not
/// an act on behalf of a reception, so it never goes through <see cref="PlatformOwner.Gate"/> or <see cref="Identity.Bind"/>.</summary>
public static class PlatformEndpoints
{
    static CurrentUser Operator(HttpContext ctx)
    {
        var sub = ctx.User.FindFirst("sub")?.Value;
        if (!PlatformOwner.Is(ctx.User) || ctx.User.FindFirst("tenant_id") is not null || string.IsNullOrEmpty(sub)) throw new AccessDeniedException();
        return new CurrentUser { Subject = "platform:" + sub, Role = "platform", Name = ctx.User.FindFirst("name")?.Value ?? "Dueño de plataforma" };
    }

    /// <summary>Creates the hospital's tenant on the general number if Hospital lists it (no Member, agent off), or pauses/resumes it. A hospital
    /// that already uses Recepción is never moved here: its people would lose their inbox.</summary>
    public static async Task<IResult> Put(Guid tenantId, ReminderClinicInput input, HttpContext ctx, CrmDb db, TenantScope scope, IConfiguration config, IHttpClientFactory factory, CancellationToken ct)
    {
        var u = Operator(ctx); scope.Id = tenantId;
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is { Mode: not "reminders" }) return Results.Conflict(new { title = "Este hospital ya usa Recepción: sus recordatorios salen por su propio número.", code = "has_reception" });
        var action = "reminder_clinic." + (input.Enabled ? "enabled" : "paused");
        if (tenant is null)
        {
            if (tenantId == Guid.Empty || !(await PlatformOwner.ListedByHospital(ctx, config, factory, ct)).TryGetValue(tenantId, out var listed)) return Results.NotFound();
            // The hospital's own zone: the day-before reminder goes out at 09:00 there, and dates and hours are written in it.
            tenant = new Tenant { Id = tenantId, Name = listed.Name, Mode = "reminders", AgentEnabled = false }; if (listed.TimeZone is { } zone) tenant.TimeZone = zone;
            db.Tenants.Add(tenant); action = "reminder_clinic.provisioned";
        }
        tenant.RemindersEnabled = input.Enabled;
        CrmEndpoints.Audit(db, scope, u, action, tenantId); await db.SaveChangesAsync(ct);
        return Results.Ok(new { tenantId, mode = tenant.Mode, enabled = tenant.RemindersEnabled });
    }

    /// <summary>A16: the hospital contracted Recepción. It leaves the general number; its rows there never attempted are deleted, so its own
    /// number can create them (one row per appointment, start and window); the sent ones stay, for a later BAJA's withdrawal. Its
    /// Administrador can now open Recepción by signing in. Its own number sends nothing until an Administrador turns reminders on.</summary>
    public static async Task<IResult> Delete(Guid tenantId, HttpContext ctx, CrmDb db, TenantScope scope, CancellationToken ct)
    {
        var u = Operator(ctx); scope.Id = tenantId;
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId && t.Mode == "reminders", ct);
        if (tenant is null) return Results.NotFound();
        tenant.Mode = "reception"; tenant.RemindersEnabled = false;
        await db.AppointmentReminders.Where(r => r.ChannelId == null && r.AttemptedAt == null).ExecuteDeleteAsync(ct);
        CrmEndpoints.Audit(db, scope, u, "reminder_clinic.removed", tenantId); await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>What on the general number nobody else will look at: sends whose delivery is unknown, cancellations Hospital did not confirm and withdrawals it refused,
    /// of the last seven days (by appointment; a refused withdrawal by its BAJA), newest first. States only: no phone, no name, no patient. Every read is audited in each hospital it shows.</summary>
    public static async Task<IReadOnlyList<GeneralDoubt>> Doubts(HttpContext ctx, CrmDb db, TenantScope scope, CancellationToken ct)
    {
        var u = Operator(ctx); var since = DateTimeOffset.UtcNow.AddDays(-7);
        var doubts = await db.AppointmentReminders.IgnoreQueryFilters().AsNoTracking().Where(r => r.ChannelId == null && (
                (r.Status == "uncertain" || r.Status == "cancelling") && r.StartsAt > since
                // A refused withdrawal is dated by its BAJA (the opt-out list keeps its last instant), not by the appointment, which may be long past.
                || r.Reason != null && r.Reason.Contains(AppointmentReminderService.WithdrawalRefused) && db.GeneralOptOuts.Any(o => o.PhoneHash == r.RecipientHash && o.CreatedAt > since)))
            .OrderByDescending(r => r.StartsAt).Take(500).Select(r => new GeneralDoubt(r.Id, r.TenantId, r.StartsAt, r.Status, r.Reason)).ToListAsync(ct);
        // The operator read these hospitals' rows: each one keeps that in its own audit trail.
        foreach (var tenant in doubts.Select(d => d.TenantId).Distinct()) { scope.Id = tenant; CrmEndpoints.Audit(db, scope, u, "reminder_doubts.read", tenant); await db.SaveChangesAsync(ct); }
        return doubts;
    }

    public static void MapPlatform(this WebApplication app)
    {
        app.MapGet("/platform/reminder-clinics/doubts", Doubts).RequireAuthorization();
        app.MapPut("/platform/reminder-clinics/{tenantId:guid}", Put).RequireAuthorization();
        app.MapDelete("/platform/reminder-clinics/{tenantId:guid}", Delete).RequireAuthorization();
    }
}
