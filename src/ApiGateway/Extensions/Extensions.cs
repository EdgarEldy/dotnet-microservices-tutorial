using System.Net;
using ApiGateway.Config;
using Common.Lib.Exceptions;
using Microsoft.AspNetCore.HttpOverrides;

namespace ApiGateway.Extensions;

/// <summary>
/// Every registration of api-gateway, grouped here (the dotnet/eShop convention) so that
/// Program.cs stays a short, readable pipeline.
/// </summary>
public static class Extensions
{
    /// <summary>IP addresses of the proxies allowed to set X-Forwarded-For (none by default).</summary>
    public const string TrustedProxiesKey = "ForwardedHeaders:TrustedProxies";

    public static IHostApplicationBuilder AddApplicationServices(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        // JwtBearer scheme shared through ServiceDefaults, used by JwtValidationMiddleware.
        builder.AddDefaultAuthentication();

        // Redis-backed rate limiter on POST /api/v1/Auth/Login.
        builder.AddRateLimiterPolicies();

        builder.AddTrustedForwardedHeaders();

        // YARP routes to the logical service names, resolved through service discovery.
        services.AddServiceDiscoveryReverseProxy();

        // The gateway's own errors become ProblemDetails through Common.Lib's handler; downstream
        // responses pass through untouched.
        services.AddProblemDetails();
        services.AddExceptionHandler<GlobalExceptionHandler>();

        return builder;
    }

    /// <summary>
    /// The login rate limiter partitions by client IP. When the gateway is deployed behind an
    /// ingress or a load balancer, RemoteIpAddress is that proxy's address, which would put every
    /// client in one shared bucket: listing the proxy under ForwardedHeaders:TrustedProxies makes
    /// UseForwardedHeaders restore the real client IP from X-Forwarded-For. Only listed proxies are
    /// trusted (ASP.NET Core's loopback defaults are cleared), so a client cannot pick its own
    /// bucket by sending the header itself. Locally, under AppHost, every caller is localhost.
    /// </summary>
    private static void AddTrustedForwardedHeaders(this IHostApplicationBuilder builder)
    {
        var trustedProxies = builder.Configuration.GetSection(TrustedProxiesKey).Get<string[]>() ?? [];

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            // ForwardedHeadersMiddleware trusts EVERY caller when both lists are empty, so with no
            // trusted proxy configured the header must not be processed at all.
            options.ForwardedHeaders = trustedProxies.Length == 0 ? ForwardedHeaders.None : ForwardedHeaders.XForwardedFor;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (var proxy in trustedProxies)
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }
        });
    }
}
