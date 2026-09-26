namespace Order.API.Security;

/// <summary>
/// Permission policy names, resolved by ServiceDefaults' RESOURCE:ACTION policy provider against
/// the "permission" claims identity-api embeds in the access token.
/// </summary>
public static class OrderPermissions
{
    public const string Read = "ORDER:READ";
    public const string Write = "ORDER:WRITE";

    /// <summary>The identity-api role whose members may read every order.</summary>
    public const string AdminRole = "Admin";
}
