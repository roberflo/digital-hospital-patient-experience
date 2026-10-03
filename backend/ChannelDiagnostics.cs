using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

public static class ChannelDiagnostics
{
    public static void MapChannelDiagnostics(this WebApplication app)
    {
        app.MapPost("/api/channels/{id:guid}/diagnostics",async(Guid id,CrmDb db,CurrentUser user,KapsoClient kapso,IConfiguration config)=>{
            user.RequireAdmin();var row=await db.Channels.SingleOrDefaultAsync(x=>x.Id==id);if(row is null)return Results.NotFound();
            if(row.PhoneNumberId=="demo")return Results.BadRequest(new{title="Canal sintético: registra un número Kapso para comprobar su conexión"});
            var path="whatsapp/phone_numbers/"+Uri.EscapeDataString(row.PhoneNumberId);
            var number=Data(await kapso.Platform(HttpMethod.Get,path));
            var health=Data(await kapso.Platform(HttpMethod.Get,path+"/health"));
            var webhooks=Data(await kapso.Platform(HttpMethod.Get,path+"/webhooks?per_page=100"));
            var expected=config["KAPSO_WEBHOOK_URL"];
            var hooks=webhooks.ValueKind==JsonValueKind.Array?webhooks.EnumerateArray().ToArray():[];
            var match=hooks.FirstOrDefault(x=>Text(x,"url")==expected&&Text(x,"kind")=="kapso"&&Bool(x,"active"));
            var configured=match.ValueKind==JsonValueKind.Object;
            var receives=configured&&match.TryGetProperty("events",out var events)&&events.EnumerateArray().Any(x=>x.GetString()=="whatsapp.message.received");
            var signatureMatches=configured&&!string.IsNullOrEmpty(config["KAPSO_WEBHOOK_SECRET"])&&Text(match,"secret_key")==config["KAPSO_WEBHOOK_SECRET"];
            // Never return provider credentials, webhook secrets, custom headers or raw errors.
            var checks=health.TryGetProperty("checks",out var checksObject)?checksObject.EnumerateObject().Select(x=>new{name=x.Name,passed=Bool(x.Value,"passed")}).ToArray():[];
            return Results.Ok(new{checkedAt=DateTimeOffset.UtcNow,kind=Text(number,"kind"),coexistence=Bool(number,"is_coexistence"),providerStatus=Text(health,"status"),checks,
                activeWebhooks=hooks.Count(x=>Bool(x,"active")),webhookUrlConfigured=!string.IsNullOrEmpty(expected),crmWebhookFound=configured,receivesMessages=receives,signatureMatches,row.LastWebhookAt,row.Enabled,
                sendEnabled=config["SEND_ENABLED"]=="true",manualSendEnabled=kapso.CanSend(true),ready=Text(health,"status")=="healthy"&&row.Enabled&&kapso.CanSend(true)&&receives&&signatureMatches});
        }).RequireAuthorization();
    }
    static JsonElement Data(JsonElement value)=>value.TryGetProperty("data",out var data)?data:value;
    static string? Text(JsonElement value,string key)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
    static bool Bool(JsonElement value,string key)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.True;
}
