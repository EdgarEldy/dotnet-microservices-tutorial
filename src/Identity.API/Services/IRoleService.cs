namespace Identity.API.Services;

/// <summary>
/// Roles and the RESOURCE:ACTION permissions they grant.
/// </summary>
public interface IRoleService
{
    /// <summary>The distinct permissions granted by <paramref name="roleNames"/>, sorted.</summary>
    Task<IReadOnlyList<string>> GetPermissionsAsync(IEnumerable<string> roleNames, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the baseline permissions, the Admin and User roles and their grants when missing.
    /// Idempotent: safe to run on every startup.
    /// </summary>
    Task SeedAsync(CancellationToken cancellationToken);
}
