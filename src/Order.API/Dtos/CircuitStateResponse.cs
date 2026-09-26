namespace Order.API.Dtos;

/// <summary>
/// The circuit breaker guarding one downstream service: Closed, Open, HalfOpen or Isolated, when it
/// last changed (null until the first change) and, while open, for how long it stays open.
/// </summary>
public sealed record CircuitStateResponse(
    string Service,
    string State,
    DateTimeOffset? LastChangedAt,
    TimeSpan? BreakDuration);
