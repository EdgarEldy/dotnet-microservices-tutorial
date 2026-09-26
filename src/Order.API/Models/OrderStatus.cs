namespace Order.API.Models;

/// <summary>
/// The Saga's states. An order starts Pending and moves exactly once, driven by notification-worker's
/// outcome event: to Confirmed (OrderConfirmedEvent) or to ConfirmationFailed (NotificationFailedEvent).
/// </summary>
public enum OrderStatus
{
    Pending,
    Confirmed,
    ConfirmationFailed,
}
