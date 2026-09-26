using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Order.API.Clients.Fallbacks;

/// <summary>
/// Maps <see cref="DownstreamServiceUnavailableException"/> (rethrown unwrapped by OrderService) to
/// a 503 ProblemDetails. Registered before Common.Lib's GlobalExceptionHandler, which handles
/// everything else: 503 is specific to order-api's downstream calls, so it stays here rather than
/// widening Common.Lib's allow-list.
/// </summary>
public sealed class DownstreamServiceUnavailableExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<DownstreamServiceUnavailableExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not DownstreamServiceUnavailableException unavailable)
        {
            return false;
        }

        logger.LogWarning("Request {Method} {Path} answered 503: {Service} unavailable",
            httpContext.Request.Method, httpContext.Request.Path, unavailable.ServiceName);

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Service Unavailable",
                Detail = unavailable.Message,
            },
        });
    }
}
