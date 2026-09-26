using Microsoft.Extensions.Options;

namespace Order.API.Clients;

/// <summary>
/// Rejects at startup a <see cref="DownstreamResilienceOptions"/> that Polly would refuse (or that
/// makes no sense), with a message naming the configuration key, instead of failing on the first order.
/// </summary>
public sealed class DownstreamResilienceOptionsValidator : IValidateOptions<DownstreamResilienceOptions>
{
    // Polly's own lower bounds for the circuit breaker and timeout strategies.
    private static readonly TimeSpan MinimumWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MinimumTimeout = TimeSpan.FromMilliseconds(10);

    public ValidateOptionsResult Validate(string? name, DownstreamResilienceOptions options)
    {
        const string prefix = DownstreamResilienceOptions.SectionName;
        var failures = new List<string>();

        if (options.FailureRatio is <= 0 or > 1)
        {
            failures.Add($"{prefix}:FailureRatio must be greater than 0 and at most 1.");
        }

        if (options.MinimumThroughput < 2)
        {
            failures.Add($"{prefix}:MinimumThroughput must be at least 2.");
        }

        if (options.SamplingDuration < MinimumWindow)
        {
            failures.Add($"{prefix}:SamplingDuration must be at least {MinimumWindow}.");
        }

        if (options.BreakDuration < MinimumWindow)
        {
            failures.Add($"{prefix}:BreakDuration must be at least {MinimumWindow}.");
        }

        if (options.RetryCount is < 1 or > 10)
        {
            failures.Add($"{prefix}:RetryCount must be between 1 and 10.");
        }

        if (options.RetryDelay < TimeSpan.Zero)
        {
            failures.Add($"{prefix}:RetryDelay must not be negative.");
        }

        if (options.AttemptTimeout < MinimumTimeout)
        {
            failures.Add($"{prefix}:AttemptTimeout must be at least {MinimumTimeout}.");
        }

        if (options.TotalTimeout <= options.AttemptTimeout)
        {
            failures.Add($"{prefix}:TotalTimeout must be greater than AttemptTimeout.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
