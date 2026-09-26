using Contracts;

namespace Notification.Worker.Services;

/// <summary>
/// The single place every outbound e-mail of the system leaves from, whichever business event
/// triggered it (the README's "IEmailNotification.Worker"). This tutorial logs the e-mail instead
/// of sending it.
/// </summary>
public interface IEmailNotification
{
    /// <summary>Sends the order confirmation e-mail.</summary>
    /// <exception cref="EmailDeliveryException">The e-mail could not be delivered.</exception>
    Task SendOrderConfirmationAsync(OrderCreatedEvent order, CancellationToken cancellationToken);

    /// <summary>Sends the account activation e-mail, with its e-mail confirmation link.</summary>
    Task SendAccountActivationAsync(UserRegisteredEvent registration, CancellationToken cancellationToken);

    /// <summary>Sends the password reset e-mail, with its reset token.</summary>
    Task SendPasswordResetAsync(PasswordResetRequestedEvent resetRequest, CancellationToken cancellationToken);
}
