namespace Contracts;

/// <summary>
/// Published by identity-api on the Kafka topic "identity-events.password-reset-requested" when
/// a password reset is requested for an existing account. notification-worker consumes it to
/// send the reset e-mail.
/// </summary>
/// <param name="UserId">Identifier of the user who asked for the reset.</param>
/// <param name="Email">Address the reset e-mail goes to.</param>
/// <param name="ResetToken">
/// URL-safe (Base64Url) password-reset token, to pass back unchanged to
/// POST /api/v1/Auth/ResetPassword.
/// </param>
public record PasswordResetRequestedEvent(int UserId, string Email, string ResetToken);
