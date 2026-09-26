using Microsoft.AspNetCore.Diagnostics;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace ApiGateway.Config;

/// <summary>
/// The gateway's routes and clusters, declared in code. Each cluster has a single destination
/// whose address is a logical service name (https+http://catalog-api): the service discovery
/// destination resolver turns it into the addresses AppHost injected through
/// .WithReference(...), so no host:port is ever written here and no refresh loop is needed.
/// </summary>
public static class YarpServiceDiscoveryConfig
{
    public const string IdentityCluster = "identity-api";
    public const string CatalogCluster = "catalog-api";
    public const string CustomerCluster = "customer-api";
    public const string OrderCluster = "order-api";

    public const string LoginRouteId = "identity-login";

    // A catch-all parameter also matches an empty segment, so "/api/v1/Customers/{**catch-all}"
    // covers the bare "/api/v1/Customers" collection route as well.
    public static IReadOnlyList<RouteConfig> Routes { get; } =
    [
        // POST Login gets its own route, only to carry the Redis-backed rate limiter policy.
        // A lower Order wins over the generic Auth route below.
        new RouteConfig
        {
            RouteId = LoginRouteId,
            ClusterId = IdentityCluster,
            Order = -1,
            RateLimiterPolicy = RateLimiterPolicies.LoginPolicy,
            Match = new RouteMatch { Path = "/api/v1/Auth/Login", Methods = ["POST"] },
        },
        Route("identity-auth", IdentityCluster, "/api/v1/Auth/{**catch-all}"),
        Route("catalog", CatalogCluster, "/api/v1/Catalog/{**catch-all}"),
        Route("customers", CustomerCluster, "/api/v1/Customers/{**catch-all}"),

        // Resolves once AppHost references order-api (feature/order-api).
        Route("orders", OrderCluster, "/api/v1/Orders/{**catch-all}"),
    ];

    public static IReadOnlyList<ClusterConfig> Clusters { get; } =
    [
        Cluster(IdentityCluster),
        Cluster(CatalogCluster),
        Cluster(CustomerCluster),
        Cluster(OrderCluster),
    ];

    /// <summary>
    /// Registers YARP with the in-code routes and clusters and the service discovery resolver.
    /// Request headers, Authorization included, are forwarded as they are by default.
    /// </summary>
    public static IServiceCollection AddServiceDiscoveryReverseProxy(this IServiceCollection services)
    {
        services.AddReverseProxy()
            .LoadFromMemory(Routes, Clusters)
            .AddServiceDiscoveryDestinationResolver()
            .AddTransforms(context =>
            {
                // Forward the client's Host header instead of the destination's: services build
                // absolute URLs (the Location of a 201 CreatedAtAction) from it, and without this
                // they would hand the client their internal address instead of the gateway's.
                context.AddOriginalHost(true);

                context.AddResponseTransform(transform =>
                {
                    // A downstream response passes through untouched: the gateway never rewraps
                    // it, even when it is an error with an empty body. The status code pages only
                    // apply to the gateway's own errors (e.g. 502 when no destination answered,
                    // in which case ProxyResponse is null).
                    if (transform.ProxyResponse is not null
                        && transform.HttpContext.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
                    {
                        statusCodePages.Enabled = false;
                    }

                    return ValueTask.CompletedTask;
                });
            });

        return services;
    }

    private static RouteConfig Route(string routeId, string clusterId, string path) => new()
    {
        RouteId = routeId,
        ClusterId = clusterId,
        Match = new RouteMatch { Path = path },
    };

    private static ClusterConfig Cluster(string serviceName) => new()
    {
        ClusterId = serviceName,
        Destinations = new Dictionary<string, DestinationConfig>
        {
            [serviceName] = new DestinationConfig { Address = $"https+http://{serviceName}" },
        },
    };
}
