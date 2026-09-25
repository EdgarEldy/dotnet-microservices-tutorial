namespace Common.Lib.Exceptions;

/// <summary>
/// Thrown by a service when the requested resource does not exist.
/// Mapped to a 404 Not Found ProblemDetails by <see cref="GlobalExceptionHandler"/>.
/// </summary>
public class ResourceNotFoundException : Exception
{
    public ResourceNotFoundException(string message)
        : base(message)
    {
    }

    public ResourceNotFoundException(string resourceName, object key)
        : base($"{resourceName} with id '{key}' was not found.")
    {
    }
}
