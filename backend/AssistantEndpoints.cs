using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
namespace Recepcion;

public static class AssistantEndpoints
{
    public static void MapAssistant(this WebApplication app)
    {
        app.MapPost("/api/assistant", async (AssistantInput input, CrmDb db, TenantScope scope, CurrentUser user, IConfiguration config, IHttpClientFactory factory, CancellationToken ct) =>
        {
            Rules.Required(input.Message, 2000);
            if (input.ContactId is { } selected && !await db.Contacts.AnyAsync(x => x.Id == selected, ct)) return Results.NotFound();
            // Deliberately send anonymous counts only. Patient names, IDs, phones, notes,
            // opportunity titles and existing message contents never enter this prompt.
            var counts = new { contacts = await db.Contacts.CountAsync(ct), waitingForHumans = await db.Conversations.CountAsync(x => x.Status == "human", ct), withAgent = await db.Conversations.CountAsync(x => x.Status == "agent", ct), followups = await db.Opportunities.GroupBy(x => x.Stage).Select(g => new { stage = g.Key, count = g.Count() }).ToListAsync(ct) };
            var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = "Eres el asistente de recepción. Responde en español y brevemente con las métricas anónimas proporcionadas. No tienes fichas ni historiales de pacientes: no inventes sus datos. Puedes guardar una nota o crear un seguimiento ÚNICAMENTE si el usuario pide esa acción explícitamente y seleccionó un contacto; la aplicación mantiene esa selección en privado. Nunca prescribas ni des instrucciones clínicas. Para preguntas médicas indica que debe responder el doctor. Si no hay contacto seleccionado, solicita seleccionarlo en la interfaz. Datos: " + JsonSerializer.Serialize(counts) + ". Contacto seleccionado: " + (input.ContactId != null) }, new JsonObject { ["role"] = "user", ["content"] = input.Message } };
            object[] tools = [Def("create_note", "Guardar nota indicada por el usuario en el contacto seleccionado", new { note = new { type = "string" } }, ["note"]), Def("create_followup", "Crear seguimiento solicitado para el contacto seleccionado", new { title = new { type = "string" } }, ["title"])];
            using var http = factory.CreateClient(); http.Timeout = TimeSpan.FromSeconds(60);
            var executed = new HashSet<string>();
            for (var turn = 0; turn < 3; turn++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, (config["AI_BASE_URL"] ?? "https://integrate.api.nvidia.com/v1").TrimEnd('/') + "/chat/completions"); request.Headers.Authorization = new("Bearer", config["NVIDIA_API_KEY"]); request.Content = JsonContent.Create(new { model = config["AI_MODEL"] ?? "nvidia/nemotron-3-super-120b-a12b", messages, tools, temperature = 0.2, max_tokens = 1200, stream = false, chat_template_kwargs = new { enable_thinking = false } });
                using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode(); var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct); var reply = json.GetProperty("choices")[0].GetProperty("message");
                if (!reply.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() == 0) return Results.Ok(new { answer = reply.GetProperty("content").GetString() ?? "No se obtuvo una respuesta. Inténtalo con una pregunta más concreta." });
                messages.Add(JsonNode.Parse(reply.GetRawText()));
                foreach (var call in calls.EnumerateArray())
                {
                    var name = call.GetProperty("function").GetProperty("name").GetString()!; using var args = JsonDocument.Parse(call.GetProperty("function").GetProperty("arguments").GetString()!); var a = args.RootElement; string result;
                    if (input.ContactId is null) result = "Selecciona un contacto antes de guardar cambios.";
                    else if (!executed.Add(name)) result = "Esta acción ya se realizó en este turno.";
                    else if (name is "create_note" or "create_followup")
                    {
                        if (name == "create_note") db.Add(new Activity { TenantId = scope.Id, ContactId = input.ContactId, Actor = user.Name + " · asistente", ActorRole = user.Role, ActorSubject = user.Subject, Body = Rules.Required(a.GetProperty("note").GetString(), 2000) });
                        else db.Add(new Opportunity { TenantId = scope.Id, ContactId = input.ContactId.Value, Title = Rules.Required(a.GetProperty("title").GetString()) });
                        CrmEndpoints.Audit(db, scope, user, "assistant." + name, input.ContactId.Value); await db.SaveChangesAsync(ct); result = "Guardado correctamente";
                    }
                    else result = "Herramienta no autorizada";
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.GetProperty("id").GetString(), ["content"] = result });
                }
            }
            return Results.Ok(new { answer = "Se alcanzó el límite de acciones. Revisa el historial para ver los cambios realizados." });
        }).RequireAuthorization().RequireRateLimiting("assistant");
    }
    static object Def(string name, string description, object properties, string[] required) => new { type = "function", function = new { name, description, parameters = new { type = "object", properties, required, additionalProperties = false } } };
}
public record AssistantInput(string Message, Guid? ContactId);
