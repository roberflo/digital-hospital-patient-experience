namespace Recepcion;

// What this installation can do, as booleans only: never a key, a URL or a secret.
// It reads configuration, not Kapso, so the onboarding guide can ask on every load.
public record InstallationReadiness(bool KapsoKey, bool WebhookUrl, bool WebhookSecret, bool AgentModel, bool AutoSend);
public static class InstallationEndpoints
{
    public static void MapInstallation(this WebApplication app) =>
        app.MapGet("/api/platform/installation", Read).RequireAuthorization().WithMetadata(new PlatformOperable());

    public static InstallationReadiness Read(CurrentUser user, IConfiguration config)
    {
        user.RequireAdminOrPlatform();
        return new(
            !string.IsNullOrEmpty(config["KAPSO_API_KEY"]),
            // Same rule WhatsAppOnboarding.EnsureWebhook applies before registering reception.
            Uri.TryCreate(config["KAPSO_WEBHOOK_URL"], UriKind.Absolute, out var url) && url.Scheme == "https",
            !string.IsNullOrEmpty(config["KAPSO_WEBHOOK_SECRET"]),
            // AgentRuntime needs the primary provider (OpenAI is only its fallback) and automatic sending.
            !string.IsNullOrEmpty(config["NVIDIA_API_KEY"]),
            config["SEND_ENABLED"] == "true");
    }
}
