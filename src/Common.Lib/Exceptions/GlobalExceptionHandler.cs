using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Common.Lib.Exceptions;

/// <summary>
/// Turns every unhandled exception into an RFC 9457 ProblemDetails response.
/// Each service registers it with <c>AddProblemDetails()</c> and
/// <c>AddExceptionHandler&lt;GlobalExceptionHandler&gt;()</c>, then calls <c>UseExceptionHandler()</c>.
/// </summary>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var problemDetails = exception switch
        {
            ValidationException validationException => CreateValidationProblem(validationException),
            ResourceNotFoundException => CreateProblem(
                StatusCodes.Status404NotFound, "Resource not found", exception.Message),
            BusinessRuleException => CreateProblem(
                StatusCodes.Status422UnprocessableEntity, "Business rule violation", exception.Message),
            // Thrown by the server itself for a faulty request (body too large, request timeout,
            // malformed body): it already carries the right 4xx status, it is not a server failure.
            BadHttpRequestException badRequest => CreateProblem(
                badRequest.StatusCode, ReasonPhrases.GetReasonPhrase(badRequest.StatusCode), exception.Message),
            _ => CreateProblem(
                StatusCodes.Status500InternalServerError,
                "An unexpected error occurred",
                "The server could not complete the request. Use the traceId to correlate with the logs."),
        };

        if (problemDetails.Status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception while processing {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            logger.LogWarning("Request {Method} {Path} failed with {StatusCode}: {Message}",
                httpContext.Request.Method, httpContext.Request.Path, problemDetails.Status, exception.Message);
        }

        httpContext.Response.StatusCode = problemDetails.Status!.Value;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception,
        });
    }

    private static ProblemDetails CreateProblem(int status, string title, string detail) => new()
    {
        Status = status,
        Title = title,
        Detail = detail,
    };

    private static ValidationProblemDetails CreateValidationProblem(ValidationException exception)
    {
        // A failure raised for the whole object has no property name: keep it under an empty key,
        // the same convention ASP.NET Core's own model validation uses.
        var errors = exception.Errors
            .GroupBy(failure => failure.PropertyName ?? string.Empty)
            .ToDictionary(
                group => group.Key,
                group => group.Select(failure => failure.ErrorMessage).Distinct().ToArray());

        return new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred.",
            // new ValidationException("message") carries no failures: its message is the only information.
            Detail = errors.Count == 0 ? exception.Message : null,
        };
    }
}
