using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Fallback;
using Polly.Timeout;

namespace Order.API.Clients.Fallbacks;

/// <summary>
/// The outermost strategy of a downstream pipeline. Whatever the inner strategies gave up on (an
/// open circuit, a timeout, a transient failure still there after the last retry) becomes one
/// clear <see cref="DownstreamServiceUnavailableException"/>, so the caller never waits on a dead
/// service nor sees a raw Polly or HttpClient exception. A non-transient answer, such as a 404,
/// passes through untouched: OrderService turns it into its own business error.
/// </summary>
public static class DownstreamUnavailableFallback
{
    public static FallbackStrategyOptions<HttpResponseMessage> Create(string serviceName, ILogger logger) => new()
    {
        Name = $"{serviceName}-fallback",
        ShouldHandle = args => ValueTask.FromResult(
            args.Outcome.Exception is BrokenCircuitException || HttpClientResiliencePredicates.IsTransient(args.Outcome)),
        FallbackAction = args =>
        {
            var cause = Describe(args.Outcome);
            args.Outcome.Result?.Dispose();

            logger.LogWarning(args.Outcome.Exception, "Fallback for {Service}: {Cause}", serviceName, cause);

            var message = $"The {DownstreamServices.DisplayName(serviceName)} is unavailable ({cause}), try again later.";
            return Outcome.FromExceptionAsValueTask<HttpResponseMessage>(
                new DownstreamServiceUnavailableException(serviceName, message, args.Outcome.Exception));
        },
    };

    private static string Describe(Outcome<HttpResponseMessage> outcome) => outcome.Exception switch
    {
        BrokenCircuitException => "circuit open",
        TimeoutRejectedException => "timed out",
        HttpRequestException => "not reachable",
        null => $"answered {(int)outcome.Result!.StatusCode} after the retries",
        _ => "call failed",
    };
}
