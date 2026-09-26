using System.Collections.Concurrent;
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

// Each test builds its own host with a capturing exporter added next to the pipeline
// AddServiceDefaults configures, so what gets recorded is exactly what the services export.
// The exporter's listener also sees activities of hosts started by test classes running in
// parallel: spans land in a thread-safe queue and every assertion filters on something
// unique to the test (a generated span name, route or trace ID).
public sealed class ExtensionsOpenTelemetryTests
{
    private const string MassTransitName = "MassTransit";
    private const string PingRoute = "/ping/{id}";

    [Fact]
    public async Task AddServiceDefaults_ShouldRecordActivity_WhenStartedFromMassTransitActivitySource()
    {
        var spans = new ConcurrentQueue<Activity>();
        await using var app = await StartAppAsync(spans, []);
        using var source = new ActivitySource(MassTransitName);
        var name = $"order-events.order-created send {Guid.NewGuid():N}";

        using (var activity = source.StartActivity(name, ActivityKind.Producer))
        {
            Assert.NotNull(activity);
        }

        var span = Assert.Single(spans, s => s.DisplayName == name);
        Assert.Equal(MassTransitName, span.Source.Name);
        Assert.Equal(ActivityKind.Producer, span.Kind);
    }

    [Fact]
    public async Task AddServiceDefaults_ShouldNotRecordActivity_WhenSourceIsNotConfigured()
    {
        await using var app = await StartAppAsync(new ConcurrentQueue<Activity>(), []);
        using var source = new ActivitySource($"Unconfigured.Source.{Guid.NewGuid():N}");

        using var activity = source.StartActivity("ignored");

        Assert.Null(activity);
    }

    [Fact]
    public async Task AddServiceDefaults_ShouldExportMassTransitMetrics_WhenMassTransitMeterRecords()
    {
        var metrics = new List<Metric>();
        await using var app = await StartAppAsync(new ConcurrentQueue<Activity>(), metrics);
        using var meter = new Meter(MassTransitName);
        var instrumentName = $"messaging.masstransit.send.{Guid.NewGuid():N}";
        var counter = meter.CreateCounter<long>(instrumentName);

        counter.Add(1);
        app.Services.GetRequiredService<MeterProvider>().ForceFlush();

        Assert.Contains(metrics, m => m.MeterName == MassTransitName && m.Name == instrumentName);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task AddServiceDefaults_ShouldRecordNoServerSpan_WhenRequestTargetsHealthEndpoint(string healthPath)
    {
        var spans = new ConcurrentQueue<Activity>();
        await using var app = await StartAppAsync(spans, []);
        using var client = app.GetTestClient();
        var healthTraceId = ActivityTraceId.CreateRandom();
        var pingTraceId = ActivityTraceId.CreateRandom();

        // A W3C traceparent per request: an unfiltered server span would join that trace ID,
        // which no other test uses.
        using var healthResponse = await SendAsync(client, healthPath, healthTraceId);
        healthResponse.EnsureSuccessStatusCode();
        // A normal request afterwards: once its span is exported, the earlier health
        // request has been fully processed too.
        using var pingResponse = await SendAsync(client, $"/ping/{Guid.NewGuid():N}", pingTraceId);
        pingResponse.EnsureSuccessStatusCode();
        await WaitForServerSpanAsync(spans, pingTraceId);

        Assert.DoesNotContain(spans, s => s.TraceId == healthTraceId);
    }

    [Fact]
    public async Task AddServiceDefaults_ShouldRecordServerSpan_WhenRequestTargetsNormalEndpoint()
    {
        var spans = new ConcurrentQueue<Activity>();
        await using var app = await StartAppAsync(spans, []);
        using var client = app.GetTestClient();
        var traceId = ActivityTraceId.CreateRandom();
        var path = $"/ping/{Guid.NewGuid():N}";

        using var response = await SendAsync(client, path, traceId);
        response.EnsureSuccessStatusCode();

        var span = await WaitForServerSpanAsync(spans, traceId);
        Assert.Equal("Microsoft.AspNetCore", span.Source.Name);
        Assert.Equal(path, span.GetTagItem("url.path"));
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, ActivityTraceId traceId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("traceparent", $"00-{traceId.ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // The server span stops when the host disposes the request context, which can happen
    // just after the client has read the response: poll briefly (up to 5 s) instead of asserting at once.
    private static async Task<Activity> WaitForServerSpanAsync(ConcurrentQueue<Activity> spans, ActivityTraceId traceId)
    {
        for (var attempt = 0; attempt < 250; attempt++)
        {
            var span = spans.FirstOrDefault(s => s.Kind == ActivityKind.Server && s.TraceId == traceId);
            if (span is not null)
            {
                return span;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"No server span recorded for trace {traceId}.");
        return null!;
    }

    private static async Task<WebApplication> StartAppAsync(ConcurrentQueue<Activity> spans, List<Metric> metrics)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();

        builder.AddServiceDefaults();

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(new QueueExporter(spans))))
            .WithMetrics(meters => meters.AddInMemoryExporter(metrics));

        var app = builder.Build();
        app.MapDefaultEndpoints();
        app.MapGet(PingRoute, (string id) => Results.Ok(id));

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    // Thread-safe replacement for the in-memory exporter's List<Activity>, which is appended
    // to from whichever thread stops an activity.
    private sealed class QueueExporter(ConcurrentQueue<Activity> spans) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                spans.Enqueue(activity);
            }

            return ExportResult.Success;
        }
    }
}
