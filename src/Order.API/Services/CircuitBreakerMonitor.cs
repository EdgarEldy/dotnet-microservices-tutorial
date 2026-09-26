using System.Collections.Concurrent;
using Order.API.Clients;
using Order.API.Dtos;
using Polly.CircuitBreaker;

namespace Order.API.Services;

/// <summary>
/// Singleton. Every downstream circuit starts Closed (as Polly's do) and follows the transitions
/// its breaker reports. Polly enters HalfOpen lazily, on the first call after the break duration,
/// so an idle circuit keeps showing Open until then: that is the breaker's real state.
/// </summary>
public sealed class CircuitBreakerMonitor : ICircuitBreakerMonitor
{
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, CircuitStateResponse> _circuits = new(StringComparer.Ordinal);

    public CircuitBreakerMonitor(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;

        foreach (var service in DownstreamServices.All)
        {
            _circuits[service] = new CircuitStateResponse(service, nameof(CircuitState.Closed), LastChangedAt: null, BreakDuration: null);
        }
    }

    public void RecordStateChange(string service, CircuitState state, TimeSpan? breakDuration) =>
        _circuits[service] = new CircuitStateResponse(service, state.ToString(), _timeProvider.GetUtcNow(), breakDuration);

    public IReadOnlyList<CircuitStateResponse> GetCircuits() =>
        _circuits.Values.OrderBy(c => c.Service, StringComparer.Ordinal).ToList();
}
