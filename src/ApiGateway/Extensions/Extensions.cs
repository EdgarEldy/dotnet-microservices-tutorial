using ApiGateway.Config;
using Common.Lib.Exceptions;

namespace ApiGateway.Extensions;

/// <summary>
/// Every registration of api-gateway, grouped here (the dotnet/eShop convention) so that
/// Program.cs stays a short, readable pipeline.
/// </summary>
public static class Extensions
{
    public static IHostApplicationBuilder AddApplicationServices(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        // JwtBearer scheme shared through ServiceDefaults, used by JwtValidationMiddleware.
        builder.AddDefaultAuthentication();

        // Redis-backed rate limiter on POST /api/v1/Auth/Login.
        builder.AddRateLimiterPolicies();

        // YARP routes to the logical service names, resolved through service discovery.
        services.AddServiceDiscoveryReverseProxy();

        // The gateway's own errors become ProblemDetails through Common.Lib's handler; downstream
        // responses pass through untouched.
        services.AddProblemDetails();
        services.AddExceptionHandler<GlobalExceptionHandler>();

        return builder;
    }
}
