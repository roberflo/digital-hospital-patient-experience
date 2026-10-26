using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI;
namespace Recepcion;

/// <summary>The model gateway's configuration: which providers, which model answers first, and how long a turn and a request may take.</summary>
public sealed partial class AgentRuntime
{
    public const string DefaultModel = "nvidia/nemotron-3-super-120b-a12b";
    public const string ClaudeDefaultModel = "claude-opus-5-5";
    static TimeSpan Seconds(IConfiguration config, string key, int fallback) => TimeSpan.FromSeconds(int.TryParse(config[key], out var value) && value > 0 ? value : fallback);
    /// <summary>How long the model may take in one turn (AI_TURN_BUDGET_SECONDS); never past the worker's deadline less 20 s, so the menu still goes out.</summary>
    public static TimeSpan Budget(IConfiguration config) => Seconds(config, "AI_TURN_BUDGET_SECONDS", 90) is var budget && budget < AgentWorker.TurnDeadline - TimeSpan.FromSeconds(20) ? budget : AgentWorker.TurnDeadline - TimeSpan.FromSeconds(20);
    /// <summary>How long one model request may take (AI_ATTEMPT_TIMEOUT_SECONDS); never past the HttpClient's own timeout, which would end the request first.</summary>
    public static TimeSpan Attempt(IConfiguration config, TimeSpan httpTimeout) => Seconds(config, "AI_ATTEMPT_TIMEOUT_SECONDS", 30) is var attempt && attempt < httpTimeout ? attempt : httpTimeout;
    /// <summary>Each provider falls back to the rest of the chain, in order.</summary>
    public static IChatClient Gateway(IReadOnlyList<IChatClient> chain, Action onFallback) => chain.Count == 0 ? throw new ArgumentException("IA no configurada")
        : chain.SkipLast(1).Reverse().Aggregate(chain[^1], (rest, provider) => new FallbackChatClient(provider, rest, onFallback));

    /// <summary>The model of the gateway's first configured provider, which is the one that answers while it is up.</summary>
    public static string FirstModel(IConfiguration config) => !string.IsNullOrEmpty(config["NVIDIA_API_KEY"]) ? config["AI_MODEL"] ?? DefaultModel
        : !string.IsNullOrEmpty(config["OPENAI_API_KEY"]) && !string.IsNullOrEmpty(config["OPENAI_MODEL"]) ? config["OPENAI_MODEL"]!
        : !string.IsNullOrEmpty(config["ANTHROPIC_API_KEY"]) ? config["ANTHROPIC_MODEL"] is { Length: > 0 } claude ? claude : ClaudeDefaultModel : config["AI_MODEL"] ?? DefaultModel;

    /// <summary>One of the gateway's providers has a key (NVIDIA_API_KEY, OPENAI_API_KEY with OPENAI_MODEL, or ANTHROPIC_API_KEY).</summary>
    public static bool AiConfigured(IConfiguration config) => !string.IsNullOrEmpty(config["NVIDIA_API_KEY"]) || !string.IsNullOrEmpty(config["OPENAI_API_KEY"]) && !string.IsNullOrEmpty(config["OPENAI_MODEL"]) || !string.IsNullOrEmpty(config["ANTHROPIC_API_KEY"]);

    /// <summary>The gateway: NVIDIA NIM, then OpenAI, then Claude, each one that is configured; a failed model call goes to the next.</summary>
    IChatClient Chat()
    {
        IChatClient Provider(string key, string endpoint, string model)
        {
            var client = new OpenAI.Chat.ChatClient(model, new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = new Uri(endpoint.TrimEnd('/')), Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(1), NetworkTimeout = Attempt(config, http.Timeout) }).AsIChatClient();
            if (!new Uri(endpoint).Host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase)) return client;
            // OpenAI's own models, probed 2026-10-04: a temperature other than the default is rejected (gpt-5-nano), and tools in Chat
            // Completions need the reasoning effort stated on some (gpt-6-luna: none). So no temperature, and the effort is configuration.
            var effort = config["OPENAI_REASONING_EFFORT"];
#pragma warning disable SCME0001 // JsonPatch is how the OpenAI client sends a body field its options do not model
            return client.AsBuilder().ConfigureOptions(options => { options.Temperature = null; if (effort is { Length: > 0 }) options.RawRepresentationFactory = _ => { var raw = new OpenAI.Chat.ChatCompletionOptions(); raw.Patch.Set("$.reasoning_effort"u8, BinaryData.FromString($"\"{effort}\"")); return raw; }; }).Build();
#pragma warning restore SCME0001
        }
        // docs/usage-report.md parte B: each provider counts the calls it answered, so one that fails adds nothing and the fallback counts as itself.
        IChatClient Metered(IChatClient client, string provider, string model) => client.AsBuilder().Use(async (messages, options, inner, token) =>
        {
            var response = await inner.GetResponseAsync(messages, options, token);
            await CountUsage(provider, response.ModelId ?? model, response.Usage);
            return response;
        }, null).Build();
        var chain = new List<IChatClient>();
        if (config["NVIDIA_API_KEY"] is { Length: > 0 } nimKey)
        {
            var nimModel = config["AI_MODEL"] ?? DefaultModel;
            var nim = Provider(nimKey, config["AI_BASE_URL"] ?? "https://integrate.api.nvidia.com/v1", nimModel);
            // Thinking stays on: measured on the evals, turning it off halves latency and drops 102/107 to 82/107 (invented slots,
            // announced actions never taken). AI_DISABLE_THINKING=true is for a model that passes the evals without it. NIM only: OpenAI rejects the field.
#pragma warning disable SCME0001 // JsonPatch is how the OpenAI client sends a provider-specific body field
            if (config["AI_DISABLE_THINKING"] == "true") nim = nim.AsBuilder().ConfigureOptions(options => options.RawRepresentationFactory = _ => { var raw = new OpenAI.Chat.ChatCompletionOptions(); raw.Patch.Set("$.chat_template_kwargs"u8, BinaryData.FromString("""{"enable_thinking":false}""")); return raw; }).Build();
#pragma warning restore SCME0001
            chain.Add(Metered(nim, "nvidia", nimModel));
        }
        if (config["OPENAI_API_KEY"] is { Length: > 0 } key && config["OPENAI_MODEL"] is { Length: > 0 } model) chain.Add(Metered(Provider(key, config["OPENAI_BASE_URL"] ?? "https://api.openai.com/v1", model), "openai", model));
        if (config["ANTHROPIC_API_KEY"] is { Length: > 0 } claudeKey)
        {
            var claudeModel = config["ANTHROPIC_MODEL"] is { Length: > 0 } configured ? configured : ClaudeDefaultModel;
            // Official SDK on the same HttpClient. Claude Opus 5.5 rejects sampling parameters and always thinks (adaptive, default effort),
            // and thinking counts toward max_tokens: no temperature, and room past the agent's 4096 so a turn is not cut mid-reply.
            // ANTHROPIC_EFFORT (low|medium|high|xhigh|max) goes as output_config.effort; unset, the model's default.
            chain.Add(Metered(new Anthropic.AnthropicClient { ApiKey = claudeKey, HttpClient = http, MaxRetries = 1, Timeout = Attempt(config, http.Timeout) }
                .AsIChatClient(claudeModel)
                .AsBuilder().ConfigureOptions(options =>
                {
                    options.Temperature = null; options.MaxOutputTokens = 16000;
                    if (config["ANTHROPIC_EFFORT"] is { Length: > 0 } effort) options.RawRepresentationFactory = _ => new Anthropic.Models.Messages.MessageCreateParams { Model = claudeModel, MaxTokens = 16000, Messages = [], OutputConfig = new() { Effort = effort } };
                }).Build(), "anthropic", claudeModel));
        }
        return Gateway(chain, () => db.Activities.Add(new Activity { TenantId = scope.Id, ConversationId = conv.Id, ContactId = contact.Id, Kind = "agent_provider", Actor = "Sistema", ActorRole = "system", Body = "El proveedor principal de IA no respondió; se usó el de respaldo." }));
    }

    /// <summary>Adds one answered call and its tokens to the clinic's row for its local day (INV-U5: counters only). Atomic, and written at once:
    /// a turn that fails later still counts what was spent. No usage in the answer: the call counts, its tokens are not invented.</summary>
    async Task CountUsage(string provider, string model, UsageDetails? usage)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        long input = usage?.InputTokenCount ?? 0, output = usage?.OutputTokenCount ?? 0, cached = usage?.CachedInputTokenCount ?? 0;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AiUsage" ("Id", "TenantId", "Day", "Provider", "Model", "Calls", "InputTokens", "OutputTokens", "CachedInputTokens")
            VALUES ({Guid.NewGuid()}, {scope.Id}, {day}, {provider}, {model}, 1, {input}, {output}, {cached})
            ON CONFLICT ("TenantId", "Day", "Provider", "Model") DO UPDATE SET "Calls" = "AiUsage"."Calls" + 1, "InputTokens" = "AiUsage"."InputTokens" + EXCLUDED."InputTokens",
                "OutputTokens" = "AiUsage"."OutputTokens" + EXCLUDED."OutputTokens", "CachedInputTokens" = "AiUsage"."CachedInputTokens" + EXCLUDED."CachedInputTokens"
            """, CancellationToken.None);
    }
}

/// <summary>Repeats one failed model call on the next provider. Only the model request is
/// repeated: tools run between calls, in this process, and are never replayed.</summary>
public sealed class FallbackChatClient(IChatClient primary, IChatClient fallback, Action onFallback) : DelegatingChatClient(primary)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sent = messages.ToList();
        try { return await base.GetResponseAsync(sent, options, cancellationToken); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or AggregateException { InnerException: HttpRequestException or TaskCanceledException } or ClientResultException { Status: 0 or 408 or 429 or >= 500 })
        {
            onFallback(); return await fallback.GetResponseAsync(sent, options, cancellationToken);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) fallback.Dispose(); base.Dispose(disposing); }
}
