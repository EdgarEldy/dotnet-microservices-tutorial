namespace Common.Lib.Exceptions;

/// <summary>
/// Thrown by a service when a request is well-formed but violates a business rule
/// (a duplicate name, an order for a product that no longer exists, and so on).
/// Mapped to a 422 Unprocessable Entity ProblemDetails by <see cref="GlobalExceptionHandler"/>.
/// </summary>
public class BusinessRuleException : Exception
{
    public BusinessRuleException(string message)
        : base(message)
    {
    }

    public BusinessRuleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
