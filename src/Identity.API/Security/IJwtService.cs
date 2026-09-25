using System.Security.Claims;

namespace Identity.API.Security;

/// <summary>
/// The only component that signs tokens. Stateless: persisting refresh tokens is the job of the
/// service layer, which only ever stores <see cref="HashRefreshToken"/>'s output.
/// </summary>
public interface IJwtService
{
    /// <summary>
    /// Signs an access token carrying <paramref name="claims"/> plus a fresh "jti" and the
    /// "sid" of the refresh token family <paramref name="sessionId"/>.
    /// </summary>
    AccessToken CreateAccessToken(IEnumerable<Claim> claims, Guid sessionId);

    /// <summary>Generates a new random refresh token and its hash.</summary>
    RefreshTokenValue CreateRefreshToken();

    /// <summary>Lowercase hex SHA-256 of a raw refresh token, the only form ever stored.</summary>
    string HashRefreshToken(string refreshToken);
}

public sealed record AccessToken(string Token, string Jti, DateTimeOffset ExpiresAt);

public sealed record RefreshTokenValue(string Token, string Hash, DateTimeOffset ExpiresAt);
