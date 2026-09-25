namespace Customer.API.Security;

/// <summary>
/// Permission policy names, resolved by ServiceDefaults' RESOURCE:ACTION policy provider against
/// the "permission" claims identity-api embeds in the access token.
/// </summary>
public static class CustomerPermissions
{
    public const string Read = "CUSTOMER:READ";
    public const string Write = "CUSTOMER:WRITE";

    /// <summary>The identity-api role whose members may read any customer profile.</summary>
    public const string AdminRole = "Admin";
}
