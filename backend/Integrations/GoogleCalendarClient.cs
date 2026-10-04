using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
namespace Recepcion.Integrations;

public sealed class GoogleCalendarClient(HttpClient http, IConfiguration config, CrmDb db, TenantScope scope, HospitalClient hospital)
{
    public string AuthorizationUrl(string state) => "https://accounts.google.com/o/oauth2/v2/auth?" + Query(new() { ["client_id"] = Required("GOOGLE_CLIENT_ID"), ["redirect_uri"] = Required("GOOGLE_REDIRECT_URI"), ["response_type"] = "code", ["scope"] = "https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/calendar.calendarlist.readonly", ["access_type"] = "offline", ["prompt"] = "consent", ["state"] = state });
    public async Task<string?> Exchange(string code, CancellationToken ct) { var j = await Token(new() { ["code"] = code, ["redirect_uri"] = Required("GOOGLE_REDIRECT_URI"), ["grant_type"] = "authorization_code" }, ct); return j.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null; }
    async Task<JsonElement> Token(Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = Required("GOOGLE_CLIENT_ID"); form["client_secret"] = Required("GOOGLE_CLIENT_SECRET");
        using var res = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct); res.EnsureSuccessStatusCode(); return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
    public async Task<IReadOnlyList<CalendarChoice>> Calendars(CancellationToken ct = default)
    {
        var tenant = await db.Tenants.SingleAsync(x => x.Id == scope.Id, ct);
        if (string.IsNullOrEmpty(tenant.GoogleRefreshToken)) throw new ArgumentException("Conecta tu cuenta de Google para elegir un calendario.");
        var token = await Token(new() { ["refresh_token"] = tenant.GoogleRefreshToken, ["grant_type"] = "refresh_token" }, ct);
        var calendars = new List<CalendarChoice>();
        string? page = null;
        do
        {
            var url = "https://www.googleapis.com/calendar/v3/users/me/calendarList?minAccessRole=writer&maxResults=250";
            if (page != null) url += "&pageToken=" + Uri.EscapeDataString(page);
            using var req = Google(HttpMethod.Get, url, token.GetProperty("access_token").GetString()!);
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) throw new ArgumentException("No pudimos consultar tus calendarios. Vuelve a conectar Google para revisar el acceso.");
            var payload = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
            foreach (var item in payload.GetProperty("items").EnumerateArray())
            {
                if (item.GetProperty("accessRole").GetString() is not ("owner" or "writer")) continue;
                if (item.TryGetProperty("deleted", out var deleted) && deleted.GetBoolean()) continue;
                var name = item.TryGetProperty("summaryOverride", out var custom) ? custom.GetString() : item.GetProperty("summary").GetString();
                calendars.Add(new(item.GetProperty("id").GetString()!, name ?? "Calendario", item.TryGetProperty("primary", out var primary) && primary.GetBoolean()));
            }
            page = payload.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        } while (!string.IsNullOrEmpty(page));
        return calendars;
    }
    public async Task SelectCalendar(string id, CancellationToken ct = default)
    {
        var calendars = await Calendars(ct);
        if (!calendars.Any(x => x.Id == id)) throw new ArgumentException("Elige uno de los calendarios disponibles en tu cuenta de Google.");
        var tenant = await db.Tenants.SingleAsync(x => x.Id == scope.Id, ct);
        tenant.GoogleCalendarId = id;
        await db.SaveChangesAsync(ct);
    }
    public async Task<int> Sync(DateOnly from, int days, CancellationToken ct = default)
    {
        if (days is < 1 or > 31) throw new ArgumentException("Sincroniza entre 1 y 31 días");
        var tenant = await db.Tenants.SingleAsync(x => x.Id == scope.Id, ct);
        if (string.IsNullOrEmpty(tenant.GoogleRefreshToken) || string.IsNullOrEmpty(tenant.GoogleCalendarId)) throw new ArgumentException("Conecta Google y selecciona un calendario");
        var token = await Token(new() { ["refresh_token"] = tenant.GoogleRefreshToken, ["grant_type"] = "refresh_token" }, ct); var access = token.GetProperty("access_token").GetString()!;
        var count = 0;
        for (var i = 0; i < days; i++)
        {
            var agenda = await hospital.GetAgendaDayAsync(scope.Id, from.AddDays(i), ct);
            foreach (var row in agenda.GetProperty("rows").EnumerateArray())
            {
                var appointment = row.GetProperty("appointmentId").GetGuid(); var status = row.GetProperty("status").GetString();
                var eid = EventId(scope.Id, appointment); var basePath = "https://www.googleapis.com/calendar/v3/calendars/" + Uri.EscapeDataString(tenant.GoogleCalendarId) + "/events";
                var start = row.GetProperty("scheduledStart").GetDateTimeOffset(); var duration = row.GetProperty("durationMinutes").GetInt32();
                if (status is "cancelled-by-patient" or "cancelled-by-clinic" or "entered-in-error")
                {
                    using var req = Google(HttpMethod.Delete, basePath + "/" + eid, access); using var res = await http.SendAsync(req, ct); if (res.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Gone)) res.EnsureSuccessStatusCode();
                }
                else
                {
                    // No patient names, phone numbers, prescriptions or visit reasons leave for Google.
                    var body = new { id = eid, summary = "Consulta · " + row.GetProperty("clinicianName").GetString(), start = new { dateTime = start.ToString("O"), timeZone = tenant.TimeZone }, end = new { dateTime = start.AddMinutes(duration).ToString("O"), timeZone = tenant.TimeZone }, visibility = "private", extendedProperties = new { @private = new { source = "recepcion", appointment = appointment.ToString() } } };
                    using var req = Google(HttpMethod.Put, basePath + "/" + eid, access); req.Content = JsonContent.Create(body); using var res = await http.SendAsync(req, ct);
                    if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    {
                        using var create = Google(HttpMethod.Post, basePath, access); create.Content = JsonContent.Create(body); using var created = await http.SendAsync(create, ct); if (created.StatusCode != HttpStatusCode.Conflict) created.EnsureSuccessStatusCode();
                    }
                    else res.EnsureSuccessStatusCode();
                }
                count++;
            }
        }
        db.Audits.Add(new Audit { TenantId = scope.Id, Actor = "calendar-worker", Action = "google.synced", Resource = count.ToString() }); await db.SaveChangesAsync(ct); return count;
    }
    public static string EventId(Guid tenant, Guid appointment) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tenant + ":" + appointment))).ToLowerInvariant();
    static HttpRequestMessage Google(HttpMethod method, string url, string token) { var r = new HttpRequestMessage(method, url); r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); return r; }
    string Required(string key) => config[key] is { Length: > 0 } s ? s : throw new ArgumentException("Google Calendar no configurado");
    static string Query(Dictionary<string, string> p) => string.Join("&", p.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
}
public static class GoogleEndpoints
{
    public static void MapGoogle(this WebApplication app)
    {
        app.MapPost("/api/google/connect", async (CrmDb db, TenantScope t, CurrentUser u, GoogleCalendarClient google) =>
        {
            u.RequireAdmin(); var row = await db.Tenants.SingleAsync(x => x.Id == t.Id); var state = t.Id.ToString("N") + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var url = google.AuthorizationUrl(state); row.GoogleStateHash = Hash(state); row.GoogleStateExpires = DateTimeOffset.UtcNow.AddMinutes(10); await db.SaveChangesAsync(); return Results.Ok(new { url });
        }).RequireAuthorization();
        app.MapGet("/oauth/google/callback", async (string? code, string? state, CrmDb db, TenantScope scope, GoogleCalendarClient google, IConfiguration c) =>
        {
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || state.Length > 200 || !Guid.TryParseExact(state.Split('.')[0], "N", out var tid)) return Results.BadRequest(new { title = "Autorización inválida o cancelada" });
            scope.Id = tid; var hash = Hash(state);
            var row = await db.Tenants.SingleOrDefaultAsync(x => x.Id == tid && x.GoogleStateHash == hash && x.GoogleStateExpires > DateTimeOffset.UtcNow); if (row is null) return Results.BadRequest();
            var consumed = await db.Tenants.Where(x => x.Id == tid && x.GoogleStateHash == hash && x.GoogleStateExpires > DateTimeOffset.UtcNow).ExecuteUpdateAsync(x => x.SetProperty(v => v.GoogleStateHash, (string?)null).SetProperty(v => v.GoogleStateExpires, (DateTimeOffset?)null)); if (consumed == 0) return Results.BadRequest();
            var refresh = await google.Exchange(code, CancellationToken.None); if (string.IsNullOrEmpty(refresh)) return Results.BadRequest(new { title = "Google no entregó acceso persistente. Repite la conexión con consentimiento." });
            row.GoogleStateHash = null; row.GoogleStateExpires = null; row.GoogleRefreshToken = refresh; await db.SaveChangesAsync(); return Results.Redirect((c["FRONTEND_URL"] ?? "http://localhost:3215") + "/?view=settings&google=connected");
        });
        app.MapGet("/api/google/calendars", async (CurrentUser u, GoogleCalendarClient g, CancellationToken ct) => { u.RequireAdmin(); return Results.Ok(await g.Calendars(ct)); }).RequireAuthorization();
        app.MapPut("/api/google/calendar", async (CalendarSelection b, CurrentUser u, GoogleCalendarClient g, CancellationToken ct) => { u.RequireAdmin(); await g.SelectCalendar(b.Id, ct); return Results.Ok(); }).RequireAuthorization();
        app.MapPost("/api/google/sync", async (CalendarSync b, CurrentUser u, GoogleCalendarClient g) => { u.RequireAdmin(); return Results.Ok(new { synced = await g.Sync(b.From, b.Days) }); }).RequireAuthorization();
        app.MapDelete("/api/google", async (CrmDb db, TenantScope t, CurrentUser u) => { u.RequireAdmin(); var row = await db.Tenants.SingleAsync(x => x.Id == t.Id); row.GoogleRefreshToken = null; row.GoogleStateHash = null; row.GoogleStateExpires = null; await db.SaveChangesAsync(); return Results.Ok(); }).RequireAuthorization();
    }
    static string Hash(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
}
public record CalendarChoice(string Id, string Name, bool Primary);
public record CalendarSelection(string Id);
public record CalendarSync(DateOnly From, int Days);
public sealed class CalendarWorker(IServiceScopeFactory factory, ILogger<CalendarWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(15), ct);
            try
            {
                using var root = factory.CreateScope(); var tenants = await root.ServiceProvider.GetRequiredService<CrmDb>().Tenants.Where(x => x.GoogleRefreshToken != null && x.GoogleCalendarId != null).Select(x => new { x.Id, x.TimeZone }).ToListAsync(ct);
                foreach (var tenant in tenants)
                {
                    using var s = factory.CreateScope(); s.ServiceProvider.GetRequiredService<TenantScope>().Id = tenant.Id;
                    try { var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone)).DateTime); await s.ServiceProvider.GetRequiredService<GoogleCalendarClient>().Sync(today, 31, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Calendar sync failed {TenantId} {Type}", tenant.Id, ex.GetType().Name); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Calendar worker unavailable {Type}", ex.GetType().Name); }
        }
    }
}
