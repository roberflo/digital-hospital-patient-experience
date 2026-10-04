using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Recepcion;
using Xunit;
public sealed class InstallationTests
{
    static readonly CurrentUser Admin = new() { Role = "admin", Subject = "admin", Name = "Admin" };
    static IConfiguration Config(params (string, string?)[] values) => new ConfigurationBuilder().AddInMemoryCollection(values.Select(x => new KeyValuePair<string, string?>(x.Item1, x.Item2))).Build();
    [Fact] public void EmptyInstallationReportsNothingReady() =>
        Assert.Equal(new InstallationReadiness(false, false, false, false, false), InstallationEndpoints.Read(Admin, Config()));
    [Fact] public void ConfiguredInstallationIsReadyAndNeverReturnsTheValues()
    {
        var read = InstallationEndpoints.Read(Admin, Config(("KAPSO_API_KEY", "kapso-key-value"), ("KAPSO_WEBHOOK_URL", "https://hooks.example.test/webhooks/kapso"), ("KAPSO_WEBHOOK_SECRET", "signing-secret-value"), ("NVIDIA_API_KEY", "nim-key-value"), ("SEND_ENABLED", "true")));
        Assert.Equal(new InstallationReadiness(true, true, true, true, true), read);
        var wire = JsonSerializer.Serialize(read);
        foreach (var secret in new[] { "kapso-key-value", "signing-secret-value", "nim-key-value", "hooks.example.test" }) Assert.DoesNotContain(secret, wire);
    }
    [Theory]
    [InlineData("http://hooks.example.test/webhooks/kapso")]
    [InlineData("/webhooks/kapso")]
    [InlineData("")]
    public void WebhookUrlMustBeAbsoluteHttps(string url) =>
        Assert.False(InstallationEndpoints.Read(Admin, Config(("KAPSO_WEBHOOK_URL", url), ("KAPSO_WEBHOOK_SECRET", "s"))).WebhookUrl);
    [Fact] public void OpenAiAloneDoesNotMakeTheAgentReadyAndManualSendIsNotAutomatic()
    {
        var read = InstallationEndpoints.Read(Admin, Config(("OPENAI_API_KEY", "k"), ("OPENAI_MODEL", "m"), ("KAPSO_MANUAL_SEND_ENABLED", "true")));
        Assert.False(read.AgentModel); Assert.False(read.AutoSend);
    }
    [Theory]
    [InlineData("agent")]
    [InlineData("doctor")]
    public void OnlyAdministratorsMayRead(string role) =>
        Assert.Throws<AccessDeniedException>(() => InstallationEndpoints.Read(new CurrentUser { Role = role }, Config()));
}
