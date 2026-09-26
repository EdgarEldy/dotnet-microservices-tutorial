namespace Notification.Worker.Services;

/// <summary>
/// Thrown by <see cref="IEmailNotification"/> when an e-mail could not be delivered. The order
/// consumer turns it into a NotificationFailedEvent; any other exception is a technical failure,
/// retried instead.
/// </summary>
public sealed class EmailDeliveryException(string message) : Exception(message);
