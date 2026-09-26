using Contracts;
using MassTransit;
using Notification.Worker.Services;

namespace Notification.Worker.Consumers;

/// <summary>
/// notification-worker's half of the choreographed order Saga: sends the confirmation e-mail,
/// then always answers order-api with exactly one outcome, <see cref="OrderConfirmedEvent"/> when
/// the e-mail went out, <see cref="NotificationFailedEvent"/> when it failed. A failed e-mail is
/// never swallowed, so no order stays Pending.
///
/// No transactional outbox here: this service has no database and no state to commit, so there is
/// no "write, then publish" gap to close. The outcome is produced to Kafka inside the consume
/// context, and the OrderCreatedEvent offset is only committed once Consume, produce included, has
/// succeeded. A failed produce fails the consume, which is retried, so the outcome is delivered at
/// least once.
///
/// Idempotency: at-least-once delivery means the same OrderCreatedEvent can arrive twice, and this
/// consumer then answers twice (and logs the e-mail twice). That is harmless on order-api's side:
/// its status transitions only apply to an order that is still Pending, so a second outcome for
/// the same order is a no-op.
/// </summary>
public sealed class OrderCreatedEventConsumer(
    IEmailNotification emailNotification,
    ITopicProducer<OrderConfirmedEvent> orderConfirmedProducer,
    ITopicProducer<NotificationFailedEvent> notificationFailedProducer,
    ILogger<OrderCreatedEventConsumer> logger)
    : IConsumer<OrderCreatedEvent>
{
    public async Task Consume(ConsumeContext<OrderCreatedEvent> context)
    {
        var order = context.Message;

        try
        {
            await emailNotification.SendOrderConfirmationAsync(order, context.CancellationToken);
        }
        catch (EmailDeliveryException exception)
        {
            // Only a delivery failure compensates. Any other exception is technical: it propagates,
            // and the message is retried rather than turned into a failed order.
            logger.LogWarning(exception, "Confirmation e-mail for order {OrderId} failed, publishing NotificationFailedEvent", order.OrderId);

            await notificationFailedProducer.Produce(
                new NotificationFailedEvent(order.OrderId, exception.Message), context.CancellationToken);
            return;
        }

        await orderConfirmedProducer.Produce(new OrderConfirmedEvent(order.OrderId), context.CancellationToken);
    }
}
