using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace ServiceDefaults.Tests;

// Each test builds its own host with an in-memory exporter added next to the pipeline
// AddServiceDefaults configures, so what gets recorded is exactly what the services export.
public sealed class ExtensionsOpenTelemetryTests
{
    private const string MassTransitName = "MassTransit";
    private const string PingPath = "/ping";

    [Fact]
    public async Task AddServiceDefaults_ShouldRecordActivity_WhenStartedFromMassTransitActivitySource()
    {
        var spans = new List<Activity>();
        await using var app = await StartAppAsync(spans, []);
        using var source = new ActivitySource(MassTransitName);

        using (var activity = source.StartActivity("order-events.order-created send", ActivityKind.Producer))
        {
            Assert.NotNull(activity);
        }

        var span = Assert.Single(spans.ToArray(), s => s.Source.Name == MassTransitName);
        Assert.Equal("order-events.order-created send", span.DisplayName);
        Assert.Equal(ActivityKind.Producer, span.Kind);
    }

    [Fact]
    public async Task AddServiceDefaults_ShouldNotRecordActivity_WhenSourceIsNotConfigured()
    {
        await using var app = await StartAppAsync([], []);
        using var source = new ActivitySource("Some.Unconfigured.Source");

        using var activity = source.StartActivity("ignored");

        Assert.Null(activity);
    }

    [Fact]
    public async Task AddServiceDefaults_ShouldExportMassTransitMetrics_WhenMassTransitMeterRecords()
    {
        var metrics = new List<Metric>();
        await using var app = await StartAppAsync([], metrics);
        using var meter = new Meter(MassTransitName);
        var counter = meter.CreateCounter<long>("messaging.masstransit.send");

        counter.Add(1);
        app.Services.GetRequiredService<MeterProvider>().ForceFlush();

        Assert.Contains(metrics, m => m.MeterName == MassTransitName && m.Name == "messaging.masstransit.send");
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task AddServiceDefaults_ShouldRecordNoServerSpan_WhenRequestTargetsHealthEndpoint(string healthPath)
    {
        var spans = new List<Activity>();
        await using var app = await StartAppAsync(spans, []);
        using var client = app.GetTestClient();

        using var healthResponse = await client.GetAsync(healthPath, TestContext.Current.CancellationToken);
        healthResponse.EnsureSuccessStatusCode();
        // A normal request afterwards: once its span is exported, the earlier health
        // request has been fully processed too.
        using var pingResponse = await client.GetAsync(PingPath, TestContext.Current.CancellationToken);
        pingResponse.EnsureSuccessStatusCode();
        await WaitForServerSpanAsync(spans, PingPath);

        Assert.DoesNotContain(ServerSpans(spans), s => Equals(s.GetTagItem("url.path"), healthPath));
    }

    [Fact]
    public async Task AddServiceDefaults_ShouldRecordServerSpan_WhenRequestTargetsNormalEndpoint()
    {
        var spans = new List<Activity>();
        await using var app = await StartAppAsync(spans, []);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(PingPath, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var span = await WaitForServerSpanAsync(spans, PingPath);
        Assert.Equal("Microsoft.AspNetCore", span.Source.Name);
    }

    // The exporter's listener also sees requests of hosts started by other test classes
    // running in parallel: snapshot the list (ToArray does not enumerate) before filtering.
    private static Activity[] ServerSpans(List<Activity> spans) =>
        spans.ToArray().Where(s => s.Kind == ActivityKind.Server).ToArray();

    // The server span stops when the host disposes the request context, which can happen
    // just after the client has read the response: poll briefly instead of asserting at once.
    private static async Task<Activity> WaitForServerSpanAsync(List<Activity> spans, string path)
    {
        for (var attempt = 0; attempt < 250; attempt++)
        {
            var span = ServerSpans(spans).FirstOrDefault(s => Equals(s.GetTagItem("url.path"), path));
            if (span is not null)
            {
                return span;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"No server span recorded for {path}.");
        return null!;
    }

    private static async Task<WebApplication> StartAppAsync(List<Activity> spans, List<Metric> metrics)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();

        builder.AddServiceDefaults();

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddInMemoryExporter(spans))
            .WithMetrics(meters => meters.AddInMemoryExporter(metrics));

        var app = builder.Build();
        app.MapDefaultEndpoints();
        app.MapGet(PingPath, () => Results.Ok());

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
