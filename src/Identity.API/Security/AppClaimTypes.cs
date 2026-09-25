using Microsoft.IdentityModel.JsonWebTokens;

namespace Identity.API.Security;

/// <summary>
/// Claim names carried by identity-api's access tokens. Short JWT names are used end to end
/// (Identity is configured with them and inbound claim mapping is off), so every service reads
/// the same names it finds in the token.
/// </summary>
public static class AppClaimTypes
{
    public const string UserId = JwtRegisteredClaimNames.Sub;

    public const string UserName = JwtRegisteredClaimNames.Name;

    public const string Email = JwtRegisteredClaimNames.Email;

    public const string Role = "role";

    /// <summary>One claim per distinct RESOURCE:ACTION granted by the user's roles.</summary>
    public const string Permission = "permission";

    public const string TokenId = JwtRegisteredClaimNames.Jti;

    /// <summary>The refresh token family (login session) the access token belongs to.</summary>
    public const string SessionId = JwtRegisteredClaimNames.Sid;

    public const string ExpiresAt = JwtRegisteredClaimNames.Exp;
}
