namespace Order.API.Clients;

/// <summary>
/// Tuning of the resilience pipeline around the Refit clients, bound from "Resilience:Downstream".
/// The defaults suit a local demo: the circuit opens once half of at least 5 calls within 30 seconds
/// failed, and stays open 15 seconds. Checked at startup by <see cref="DownstreamResilienceOptionsValidator"/>.
/// </summary>
public sealed class DownstreamResilienceOptions
{
    public const string SectionName = "Resilience:Downstream";

    /// <summary>Share of failed calls within the sampling window that opens the circuit, in (0, 1].</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>Calls needed within the sampling window before the failure ratio counts (at least 2).</summary>
    public int MinimumThroughput { get; set; } = 5;

    /// <summary>The sliding window over which the failure ratio is computed.</summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the circuit stays open before a trial call is let through (half-open).</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Retries after the first attempt, for transient failures only (5xx, 408, 429, network, timeout).</summary>
    public int RetryCount { get; set; } = 2;

    /// <summary>Base delay of the exponential, jittered backoff between retries.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Timeout of one attempt (one HTTP call).</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Timeout of the whole call, retries and backoff included.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
