namespace Identity.API.Security;

/// <summary>
/// The roles seeded at startup and the permissions each one grants.
/// </summary>
public static class AppRoles
{
    public const string Admin = "Admin";

    /// <summary>Assigned to every account created through Register.</summary>
    public const string User = "User";

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> PermissionsByRole { get; } =
        new Dictionary<string, IReadOnlyList<string>>
        {
            [Admin] = AppPermissions.All,
            [User] =
            [
                AppPermissions.CustomerRead,
                AppPermissions.CustomerWrite,
                AppPermissions.OrderRead,
                AppPermissions.OrderWrite,
            ],
        };
}
