namespace Order.API.Clients;

/// <summary>
/// The downstream services order-api calls synchronously: their logical names (service discovery,
/// logs, circuit breaker diagnostics) and the wording used when one is unavailable.
/// </summary>
public static class DownstreamServices
{
    public const string CatalogApi = "catalog-api";
    public const string CustomerApi = "customer-api";

    /// <summary>Every downstream service guarded by a circuit breaker, in display order.</summary>
    public static IReadOnlyList<string> All { get; } = [CatalogApi, CustomerApi];

    /// <summary>The business name of a downstream service, as shown in an error message.</summary>
    public static string DisplayName(string serviceName) => serviceName switch
    {
        CatalogApi => "product service",
        CustomerApi => "customer service",
        _ => serviceName,
    };
}
