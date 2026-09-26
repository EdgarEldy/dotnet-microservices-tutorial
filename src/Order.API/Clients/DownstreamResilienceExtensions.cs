using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Order.API.Clients.Fallbacks;
using Order.API.Services;
using Polly;
using Polly.CircuitBreaker;

namespace Order.API.Clients;

/// <summary>
/// The "downstream" resilience pipeline of the Refit clients, from the outside in: fallback (turns
/// any give-up into a 503-mapped exception), total timeout, retry (exponential, jittered, transient
/// failures only), circuit breaker (each attempt counts), attempt timeout. The same order as the
/// standard handler, plus the fallback and this service's own settings.
/// </summary>
public static class DownstreamResilienceExtensions
{
    public const string PipelineName = "downstream";

    public static IHttpClientBuilder AddDownstreamResilience(this IHttpClientBuilder builder, string serviceName)
    {
        // ServiceDefaults adds the standard resilience handler to every HttpClient through
        // ConfigureHttpClientDefaults. Two stacked pipelines would multiply the retries and hide
        // this breaker behind the standard one, so these clients drop it and get this one instead.
        // RemoveAllResilienceHandlers is the documented way to opt a client out of the defaults, but
        // dotnet/extensions still flags it EXTEXP0001 (experimental): suppressed for this call only.
#pragma warning disable EXTEXP0001
        builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        builder.AddResilienceHandler(PipelineName, (pipeline, context) =>
        {
            var services = context.ServiceProvider;
            var options = services.GetRequiredService<IOptions<DownstreamResilienceOptions>>().Value;
            var monitor = services.GetRequiredService<ICircuitBreakerMonitor>();
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DownstreamResilienceExtensions));

            pipeline
                .AddFallback(DownstreamUnavailableFallback.Create(serviceName, logger))
                .AddTimeout(options.TotalTimeout)
                .AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = options.RetryCount,
                    Delay = options.RetryDelay,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                })
                .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    Name = $"{serviceName}-circuit-breaker",
                    FailureRatio = options.FailureRatio,
                    MinimumThroughput = options.MinimumThroughput,
                    SamplingDuration = options.SamplingDuration,
                    BreakDuration = options.BreakDuration,
                    OnOpened = args =>
                    {
                        monitor.RecordStateChange(serviceName, CircuitState.Open, args.BreakDuration);
                        logger.LogWarning(args.Outcome.Exception,
                            "Circuit breaker for {Service} opened for {BreakDuration}: calls fail fast until it half-opens",
                            serviceName, args.BreakDuration);
                        return default;
                    },
                    OnHalfOpened = _ =>
                    {
                        monitor.RecordStateChange(serviceName, CircuitState.HalfOpen, breakDuration: null);
                        logger.LogInformation(
                            "Circuit breaker for {Service} half-opened after {BreakDuration}: letting a trial call through",
                            serviceName, options.BreakDuration);
                        return default;
                    },
                    OnClosed = _ =>
                    {
                        monitor.RecordStateChange(serviceName, CircuitState.Closed, breakDuration: null);
                        logger.LogInformation("Circuit breaker for {Service} closed: calls flow normally", serviceName);
                        return default;
                    },
                })
                .AddTimeout(options.AttemptTimeout);
        });

        return builder;
    }
}
