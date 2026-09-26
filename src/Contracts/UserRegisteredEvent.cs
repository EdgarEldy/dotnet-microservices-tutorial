namespace Contracts;

/// <summary>
/// Published by identity-api on the Kafka topic "identity-events.user-registered" once a new
/// account is committed. notification-worker consumes it to send the activation e-mail.
/// </summary>
/// <param name="UserId">Identifier of the new, still unconfirmed, user.</param>
/// <param name="Email">Address the activation e-mail goes to.</param>
/// <param name="ConfirmationToken">
/// URL-safe (Base64Url) e-mail confirmation token, to pass back unchanged to
/// GET /api/v1/Auth/ConfirmEmail together with <paramref name="UserId"/>.
/// </param>
public record UserRegisteredEvent(int UserId, string Email, string ConfirmationToken);
