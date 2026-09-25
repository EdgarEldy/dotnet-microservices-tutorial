using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds the cross-cutting plumbing every service shares: OpenTelemetry, health checks,
// service discovery and resilience for outgoing HttpClients. Generated from the
// aspire-servicedefaults template, with the health endpoints this project's README requires.
// See https://aka.ms/aspire/service-defaults
public static class Extensions
{
    public const string LivenessEndpointPath = "/health/live";
    public const string ReadinessEndpointPath = "/health/ready";

    // Checks carrying this tag answer "is the process up?". Every other check
    // (database, Kafka, ...) is a readiness concern: "can it serve traffic right now?".
    public const string LiveTag = "live";

    private const string HealthEndpointsPrefix = "/health";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default: a base address such as
            // https+http://catalog-api is resolved from the configuration AppHost injects.
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(options =>
                        // Health probes run every few seconds: keep them out of the traces
                        options.Filter = context => !context.Request.Path.StartsWithSegments(HealthEndpointsPrefix))
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        // Set by AppHost for every project it runs, pointing at the Aspire dashboard.
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Default liveness check: the process is up and able to answer
            .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Mapped in every environment, unlike the template: an orchestrator needs them in
        // production too. They only return Healthy, Degraded or Unhealthy, never check
        // details, and only api-gateway is exposed outside the application network.

        // Readiness: every registered check must pass (dependencies such as the database
        // or Kafka register theirs through their Aspire client integration).
        app.MapHealthChecks(ReadinessEndpointPath);

        // Liveness: only the checks tagged "live", so a database outage makes the service
        // unready (no traffic routed to it) without getting the process restarted.
        app.MapHealthChecks(LivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(LiveTag),
        });

        return app;
    }
}
