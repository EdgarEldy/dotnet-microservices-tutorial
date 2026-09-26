using Order.API.Dtos;
using Polly.CircuitBreaker;

namespace Order.API.Services;

/// <summary>
/// Remembers the last state of each downstream circuit breaker, fed by the breakers' own
/// state-change callbacks, so the pattern can be observed at /api/v1/Orders/Diagnostics/Circuits.
/// </summary>
public interface ICircuitBreakerMonitor
{
    /// <summary>Records a transition reported by the circuit breaker of <paramref name="service"/>.</summary>
    void RecordStateChange(string service, CircuitState state, TimeSpan? breakDuration);

    /// <summary>The current state of every known downstream circuit.</summary>
    IReadOnlyList<CircuitStateResponse> GetCircuits();
}
