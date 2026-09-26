namespace Identity.API.Security;

/// <summary>
/// Thrown by the service layer when credentials or a refresh token are rejected. The message is
/// deliberately the same for every cause (unknown e-mail, wrong password, unconfirmed or locked
/// account, reused or expired token), so the response never reveals whether an account exists.
/// Mapped to a 401 ProblemDetails by <see cref="AuthenticationFailedExceptionHandler"/>; the
/// precise cause only goes to the logs and the audit trail.
/// </summary>
public sealed class AuthenticationFailedException : Exception
{
    public const string DefaultMessage = "The credentials or token supplied are invalid.";

    public AuthenticationFailedException()
        : base(DefaultMessage)
    {
    }

    public AuthenticationFailedException(string message)
        : base(message)
    {
    }

    public AuthenticationFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
