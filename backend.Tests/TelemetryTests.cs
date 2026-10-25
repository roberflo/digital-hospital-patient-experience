using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Recepcion;
using Xunit;
using Diag = System.Diagnostics;
// Hospital's docs/features/21-observabilidad/opentelemetry.spec.md: OBS-1, OBS-4 and OBS-6 on Recepción's side.
public sealed partial class AgentIntegrationTests {
    [Fact]public void NoTracerProviderWithoutACollector(){
        var services=new ServiceCollection();
        services.AddRecepcionTelemetry(new ConfigurationBuilder().Build(),new HostingEnvironment{EnvironmentName="Test"});
        Assert.Null(services.BuildServiceProvider().GetService<TracerProvider>());
    }
    [Fact]public void AnOrphanDatabaseSpanIsDroppedAndOneWithAParentIsExported(){
        using var npgsql=new Diag.ActivitySource("Npgsql");var exported=new List<Diag.Activity>();
        using(Sdk.CreateTracerProviderBuilder().AddProcessor(new OrphanDatabaseSpanFilter()).AddSource(npgsql.Name,Telemetry.Source.Name).AddInMemoryExporter(exported).Build())
        {
            npgsql.StartActivity("poll")!.Dispose();
            using(Telemetry.Source.StartActivity("agent.turn")) npgsql.StartActivity("query")!.Dispose();
        }
        Assert.Equal(["agent.turn","query"],exported.Select(x=>x.DisplayName).Order());
    }
    [Fact]public async Task AFailedTurnIsOneErroredSpanThatNamesTheJobAndNotTheFailure(){
        var spans=new List<Diag.Activity>();
        using var listener=new Diag.ActivityListener{ShouldListenTo=s=>s.Name==Telemetry.Source.Name,Sample=(ref Diag.ActivityCreationOptions<Diag.ActivityContext> _)=>Diag.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped=a=>{if(Equals(a.GetTagItem("job.id"),job.Id))lock(spans)spans.Add(a);}};
        Diag.ActivitySource.AddActivityListener(listener);
        await AgentWorker.RunTenant(Tenant(new Fake(_=>throw new InvalidOperationException("Synthetic model failure")),Sending()),scope.Id,NullLogger.Instance,CancellationToken.None);
        var span=Assert.Single(spans);
        Assert.Equal("agent.turn",span.DisplayName);Assert.Equal(Diag.ActivityStatusCode.Error,span.Status);Assert.Equal("failed",span.GetTagItem("job.status"));
        Assert.DoesNotContain(span.TagObjects,t=>t.Value?.ToString()?.Contains("Synthetic")==true);Assert.Null(span.StatusDescription);
    }
}
