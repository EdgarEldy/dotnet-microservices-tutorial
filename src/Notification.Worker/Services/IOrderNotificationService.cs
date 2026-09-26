using Contracts;

namespace Notification.Worker.Services;

/// <summary>
/// notification-worker's half of the choreographed order Saga: sends the order confirmation
/// e-mail and answers order-api with exactly one outcome event.
/// </summary>
public interface IOrderNotificationService
{
    /// <summary>
    /// Sends the confirmation e-mail of <paramref name="order"/>, then produces
    /// <see cref="OrderConfirmedEvent"/> when it went out, <see cref="NotificationFailedEvent"/> when
    /// it failed.
    /// </summary>
    Task ConfirmOrderAsync(OrderCreatedEvent order, CancellationToken cancellationToken);
}
