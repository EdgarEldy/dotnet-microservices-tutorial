using Contracts;
using MassTransit;

namespace Notification.Worker.Services;

/// <summary>
/// Sends the order confirmation e-mail, then always answers order-api with exactly one outcome:
/// <see cref="OrderConfirmedEvent"/> when the e-mail went out, <see cref="NotificationFailedEvent"/>
/// when it failed. A failed e-mail is never swallowed, so no order stays Pending by design.
///
/// No transactional outbox here: this service has no database and no state to commit, so there is
/// no "write, then publish" gap to close. It runs inside the Kafka consume context of
/// OrderCreatedEvent: the outcome is produced before Consume returns, and the OrderCreatedEvent
/// offset is only committed once Consume, produce included, has succeeded. A failed produce fails
/// the consume, which is retried, so the outcome is delivered at least once.
///
/// Accepted trade-off: if producing the outcome keeps failing after every retry, the Kafka rider
/// gives up on that message and the order stays Pending. Acceptable for this tutorial; a durable
/// fix would need a database (an outbox) that this service deliberately does not have.
///
/// Idempotency: at-least-once delivery means the same OrderCreatedEvent can arrive twice, and this
/// service then answers twice (and logs the e-mail twice). That is harmless on order-api's side:
/// its status transitions only apply to an order that is still Pending, so a second outcome for
/// the same order is a no-op.
/// </summary>
public sealed class OrderNotificationService(
    IEmailNotification emailNotification,
    ITopicProducer<OrderConfirmedEvent> orderConfirmedProducer,
    ITopicProducer<NotificationFailedEvent> notificationFailedProducer,
    ILogger<OrderNotificationService> logger)
    : IOrderNotificationService
{
    public async Task ConfirmOrderAsync(OrderCreatedEvent order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        try
        {
            await emailNotification.SendOrderConfirmationAsync(order, cancellationToken);
        }
        catch (EmailDeliveryException exception)
        {
            // Only a delivery failure compensates. Any other exception is technical: it propagates,
            // and the message is retried rather than turned into a failed order.
            logger.LogWarning(exception, "Confirmation e-mail for order {OrderId} failed, publishing NotificationFailedEvent", order.OrderId);

            await notificationFailedProducer.Produce(
                new NotificationFailedEvent(order.OrderId, exception.Message), cancellationToken);
            return;
        }

        await orderConfirmedProducer.Produce(new OrderConfirmedEvent(order.OrderId), cancellationToken);
    }
}
