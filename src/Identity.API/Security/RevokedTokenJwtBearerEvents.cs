using Identity.API.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Identity.API.Security;

/// <summary>
/// After the signature, issuer, audience and lifetime checks, rejects an access token whose
/// "jti" was blacklisted by Logout. Resolved per request (JwtBearerOptions.EventsType).
/// </summary>
public sealed class RevokedTokenJwtBearerEvents(IAuthService authService) : JwtBearerEvents
{
    public override async Task TokenValidated(TokenValidatedContext context)
    {
        var jti = context.Principal?.FindFirst(AppClaimTypes.TokenId)?.Value;

        if (string.IsNullOrEmpty(jti))
        {
            context.Fail("The access token has no jti claim.");
            return;
        }

        if (await authService.IsAccessTokenRevokedAsync(jti, context.HttpContext.RequestAborted))
        {
            context.Fail("The access token has been revoked.");
        }
    }
}
