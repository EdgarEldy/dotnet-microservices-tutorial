using Identity.API.Dtos;

namespace Identity.API.Services;

/// <summary>
/// Account lifecycle: registration, e-mail confirmation, password reset, profile.
/// Never sends an e-mail: it publishes an event and notification-worker delivers it.
/// </summary>
public interface IUserService
{
    /// <summary>
    /// Creates an unconfirmed account with the User role and publishes a UserRegisteredEvent,
    /// both in one transaction. An e-mail already registered is silently ignored, so the caller
    /// cannot tell the two cases apart.
    /// </summary>
    Task RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);

    Task ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes a PasswordResetRequestedEvent when the account exists, does nothing otherwise:
    /// the caller cannot tell the two cases apart.
    /// </summary>
    Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken);

    /// <summary>Sets the new password and revokes every refresh token of the account.</summary>
    Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken);

    Task<UserProfileResponse> GetProfileAsync(int userId, CancellationToken cancellationToken);
}
