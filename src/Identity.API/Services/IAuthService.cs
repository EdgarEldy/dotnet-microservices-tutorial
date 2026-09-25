using Identity.API.Dtos;
using Identity.API.Security;

namespace Identity.API.Services;

/// <summary>
/// Sessions: login, refresh token rotation, logout, access token revocation.
/// Every rejection is an <see cref="AuthenticationFailedException"/> with the same message.
/// </summary>
public interface IAuthService
{
    /// <summary>Checks the credentials and opens a new session (a new refresh token family).</summary>
    Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Rotates a refresh token within its family. Presenting a token already rotated or revoked,
    /// or one issued before the account's security stamp changed, revokes the whole family.
    /// </summary>
    Task<TokenResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken);

    /// <summary>Blacklists the current access token until it expires and revokes its session's family.</summary>
    Task LogoutAsync(CurrentSession session, CancellationToken cancellationToken);

    Task<bool> IsAccessTokenRevokedAsync(string jti, CancellationToken cancellationToken);
}
