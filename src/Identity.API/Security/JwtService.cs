using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Identity.API.Security;

/// <summary>
/// Signs access tokens with HMAC-SHA256 and the shared key of <see cref="JwtOptions"/>. Every
/// service that validates these tokens (api-gateway, the business services) is configured with
/// the same key, issuer and audience, and validates them on its own, without calling identity-api.
/// </summary>
public sealed class JwtService(
    IOptions<JwtOptions> jwtOptions,
    IOptions<IdentityOptions> identityOptions,
    TimeProvider timeProvider) : IJwtService
{
    private const int RefreshTokenByteLength = 64;

    private static readonly JsonWebTokenHandler TokenHandler = new();

    public AccessToken CreateAccessToken(IEnumerable<Claim> claims, Guid sessionId)
    {
        var options = jwtOptions.Value;
        var securityStampClaimType = identityOptions.Value.ClaimsIdentity.SecurityStampClaimType;

        var now = timeProvider.GetUtcNow();
        var expiresAt = now + options.AccessTokenLifetime;
        var jti = Guid.NewGuid().ToString("N");

        // The security stamp is an internal Identity value: it has no business in a bearer token.
        var tokenClaims = claims
            .Where(claim => claim.Type != securityStampClaimType
                && claim.Type != AppClaimTypes.TokenId
                && claim.Type != AppClaimTypes.SessionId)
            .Append(new Claim(AppClaimTypes.TokenId, jti))
            .Append(new Claim(AppClaimTypes.SessionId, sessionId.ToString()));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            Subject = new ClaimsIdentity(tokenClaims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(options.CreateSigningKey(), SecurityAlgorithms.HmacSha256),
        };

        return new AccessToken(TokenHandler.CreateToken(descriptor), jti, expiresAt);
    }

    public RefreshTokenValue CreateRefreshToken()
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RefreshTokenByteLength));
        var expiresAt = timeProvider.GetUtcNow() + jwtOptions.Value.RefreshTokenLifetime;

        return new RefreshTokenValue(token, HashRefreshToken(token), expiresAt);
    }

    public string HashRefreshToken(string refreshToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
}
