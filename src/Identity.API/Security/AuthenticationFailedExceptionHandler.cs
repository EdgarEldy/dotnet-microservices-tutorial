using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Identity.API.Security;

/// <summary>
/// Maps <see cref="AuthenticationFailedException"/> to a 401 ProblemDetails. Registered before
/// Common.Lib's GlobalExceptionHandler, which handles everything else: 401 is specific to this
/// service, so it stays here rather than widening Common.Lib's allow-list.
/// </summary>
public sealed class AuthenticationFailedExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<AuthenticationFailedExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not AuthenticationFailedException)
        {
            return false;
        }

        logger.LogWarning("Request {Method} {Path} rejected with 401: {Message}",
            httpContext.Request.Method, httpContext.Request.Path, exception.Message);

        httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Authentication failed",
                Detail = exception.Message,
            },
        });
    }
}
