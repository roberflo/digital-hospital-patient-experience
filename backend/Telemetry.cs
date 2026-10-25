using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
namespace Recepcion;

/// <summary>Traces and metrics over OTLP, off unless OTEL_EXPORTER_OTLP_ENDPOINT is set (Hospital's
/// docs/features/21-observabilidad/opentelemetry.spec.md, OBS-1…OBS-7). No bodies, headers, exception messages or query values;
/// the AI SDKs' own sources are left out on purpose: they can carry the conversation. Calls to Hospital carry traceparent.</summary>
public static class Telemetry
{
    public static readonly System.Diagnostics.ActivitySource Source = new("Recepcion");

    public static void AddRecepcionTelemetry(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        if (string.IsNullOrWhiteSpace(config["OTEL_EXPORTER_OTLP_ENDPOINT"])) return;
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("recepcion-api").AddAttributes([new("deployment.environment.name", env.EnvironmentName)]))
            .WithTracing(t => t.AddProcessor(new OrphanDatabaseSpanFilter()).AddSource("Npgsql", Source.Name)
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation().AddOtlpExporter())
            .WithMetrics(m => m.AddMeter("Npgsql", "System.Runtime").AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter());
    }
}

/// <summary>OBS-4: a database span with no parent is the workers' polling, not work anyone asked for: it never reaches the exporter.</summary>
public sealed class OrphanDatabaseSpanFilter : BaseProcessor<System.Diagnostics.Activity>
{
    public override void OnStart(System.Diagnostics.Activity data)
    {
        if (data.Source.Name == "Npgsql" && data.Parent is null && data.ParentSpanId == default)
            data.ActivityTraceFlags &= ~System.Diagnostics.ActivityTraceFlags.Recorded;
    }
}
