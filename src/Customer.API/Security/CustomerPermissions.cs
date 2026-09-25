namespace Customer.API.Security;

/// <summary>
/// Permission policy names, resolved by ServiceDefaults' RESOURCE:ACTION policy provider against
/// the "permission" claims identity-api embeds in the access token.
/// </summary>
public static class CustomerPermissions
{
    public const string Read = "CUSTOMER:READ";
    public const string Write = "CUSTOMER:WRITE";
}
