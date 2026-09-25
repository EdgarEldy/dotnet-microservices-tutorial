namespace Catalog.API.Security;

/// <summary>
/// The RESOURCE:ACTION permissions this service checks, as policy names: ServiceDefaults'
/// permission policy provider turns each into a requirement on the token's "permission" claims.
/// </summary>
public static class CatalogPermissions
{
    /// <summary>Create categories and products (granted to the Admin role by identity-api).</summary>
    public const string Write = "CATALOG:WRITE";
}
