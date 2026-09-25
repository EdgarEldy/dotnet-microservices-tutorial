namespace Identity.API.Security;

/// <summary>
/// The baseline RESOURCE:ACTION permissions seeded at startup. Downstream services check them
/// against the "permission" claims of the access token, without any call to identity-api.
/// </summary>
public static class AppPermissions
{
    public const string CatalogWrite = "CATALOG:WRITE";
    public const string CustomerRead = "CUSTOMER:READ";
    public const string CustomerWrite = "CUSTOMER:WRITE";
    public const string OrderRead = "ORDER:READ";
    public const string OrderWrite = "ORDER:WRITE";
    public const string AuditRead = "AUDIT:READ";

    public static IReadOnlyList<string> All { get; } =
        [CatalogWrite, CustomerRead, CustomerWrite, OrderRead, OrderWrite, AuditRead];

    /// <summary>Splits RESOURCE:ACTION into its two parts.</summary>
    public static (string Resource, string Action) Parse(string permission)
    {
        var separator = permission.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == permission.Length - 1)
        {
            throw new ArgumentException($"'{permission}' is not in the RESOURCE:ACTION format.", nameof(permission));
        }

        return (permission[..separator], permission[(separator + 1)..]);
    }
}
