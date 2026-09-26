namespace Contracts;

/// <summary>
/// Published by notification-worker when the confirmation e-mail for an order could not be sent:
/// the Saga's compensating outcome. order-api moves the order from Pending to ConfirmationFailed.
/// </summary>
public sealed record NotificationFailedEvent(int OrderId, string Reason);
