using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using RedisRateLimiting;
using StackExchange.Redis;

namespace ApiGateway.Config;

/// <summary>
/// Rate limiting of the gateway: a Redis-backed fixed window on POST /api/v1/Auth/Login only,
/// per client IP, protecting identity-api against brute-force attempts. The counters live in
/// Redis, so every gateway instance shares the same window.
/// </summary>
public static class RateLimiterPolicies
{
    public const string LoginPolicy = "login";

    public static IHostApplicationBuilder AddRateLimiterPolicies(this IHostApplicationBuilder builder)
    {
        // Aspire client integration: connection string injected by AppHost (WithReference(redis)),
        // registers IConnectionMultiplexer with health check and tracing.
        builder.AddRedisClient("redis");

        builder.Services.AddOptions<LoginRateLimitOptions>()
            .Bind(builder.Configuration.GetSection(LoginRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy<string, LoginRateLimiterPolicy>(LoginPolicy);
        });

        return builder;
    }
}

/// <summary>The "RateLimiting:Login" configuration section.</summary>
public sealed class LoginRateLimitOptions
{
    public const string SectionName = "RateLimiting:Login";

    /// <summary>Login attempts allowed per client IP within one window.</summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 5;

    /// <summary>Length of the fixed window, in seconds.</summary>
    [Range(1, int.MaxValue)]
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>
/// One Redis fixed-window limiter per client IP. A rejected request is answered with a 429
/// ProblemDetails (and Retry-After when the limiter knows it).
/// </summary>
public sealed class LoginRateLimiterPolicy(
    IConnectionMultiplexer redis,
    IOptions<LoginRateLimitOptions> options) : IRateLimiterPolicy<string>
{
    private const string UnknownClient = "unknown";

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = WriteRejectionAsync;

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        // RemoteIpAddress, restored from X-Forwarded-For by UseForwardedHeaders when a trusted
        // proxy (ForwardedHeaders:TrustedProxies) sits in front of the gateway.
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? UnknownClient;
        var limit = options.Value;

        return RedisRateLimitPartition.GetFixedWindowRateLimiter(
            $"{RateLimiterPolicies.LoginPolicy}:{clientIp}",
            _ => new RedisFixedWindowRateLimiterOptions
            {
                ConnectionMultiplexerFactory = () => redis,
                PermitLimit = limit.PermitLimit,
                Window = TimeSpan.FromSeconds(limit.WindowSeconds),
            });
    }

    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;
        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        var problemDetailsService = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests",
                Detail = "Too many login attempts from this client. Try again later.",
            },
        });
    }
}
