namespace Order.API.Clients.Fallbacks;

/// <summary>
/// Thrown by <see cref="DownstreamUnavailableFallback"/> when a downstream service cannot answer:
/// its circuit is open, the call timed out, or the retries were exhausted. Mapped to a 503
/// ProblemDetails by <see cref="DownstreamServiceUnavailableExceptionHandler"/>.
/// </summary>
public sealed class DownstreamServiceUnavailableException : Exception
{
    public DownstreamServiceUnavailableException()
    {
    }

    public DownstreamServiceUnavailableException(string message)
        : base(message)
    {
    }

    public DownstreamServiceUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public DownstreamServiceUnavailableException(string serviceName, string message, Exception? innerException)
        : base(message, innerException)
    {
        ServiceName = serviceName;
    }

    /// <summary>The logical name of the unavailable service, such as catalog-api.</summary>
    public string ServiceName { get; } = string.Empty;

    /// <summary>
    /// This exception, when <paramref name="exception"/> is one or wraps one: Refit wraps whatever
    /// the HTTP pipeline throws in an ApiRequestException.
    /// </summary>
    public static DownstreamServiceUnavailableException? FindIn(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is DownstreamServiceUnavailableException unavailable)
            {
                return unavailable;
            }
        }

        return null;
    }
}
