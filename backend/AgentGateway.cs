using System.ClientModel;
using Microsoft.Extensions.AI;
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
