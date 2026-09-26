using System.Globalization;
using System.Security.Claims;

namespace Identity.API.Security;

/// <summary>
/// The login session an authenticated request belongs to, read from its access token.
/// </summary>
public sealed record CurrentSession(int UserId, string Jti, Guid SessionId, DateTimeOffset AccessTokenExpiresAt);

public static class ClaimsPrincipalExtensions
{
    /// <summary>The "sub" claim of an access token issued by this service.</summary>
    public static int GetUserId(this ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(AppClaimTypes.UserId), NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : throw new AuthenticationFailedException("The access token has no valid subject.");

    /// <summary>The "sub", "jti", "sid" and "exp" claims of an access token issued by this service.</summary>
    public static CurrentSession GetCurrentSession(this ClaimsPrincipal principal)
    {
        var jti = principal.FindFirstValue(AppClaimTypes.TokenId);

        if (string.IsNullOrEmpty(jti)
            || !Guid.TryParse(principal.FindFirstValue(AppClaimTypes.SessionId), out var sessionId)
            || !long.TryParse(principal.FindFirstValue(AppClaimTypes.ExpiresAt), NumberStyles.None, CultureInfo.InvariantCulture, out var expiresAt))
        {
            throw new AuthenticationFailedException("The access token is missing its jti, sid or exp claim.");
        }

        return new CurrentSession(principal.GetUserId(), jti, sessionId, DateTimeOffset.FromUnixTimeSeconds(expiresAt));
    }
}
