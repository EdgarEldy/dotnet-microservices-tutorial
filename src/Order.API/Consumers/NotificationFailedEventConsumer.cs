using Contracts;
using MassTransit;
using Order.API.Services;

namespace Order.API.Consumers;

/// <summary>
/// The Saga's compensating event, consumed from the notification-events.notification-failed Kafka
/// topic: moves the order to ConfirmationFailed. Translates the message into a service call.
/// </summary>
public sealed class NotificationFailedEventConsumer(IOrderService orderService) : IConsumer<NotificationFailedEvent>
{
    public Task Consume(ConsumeContext<NotificationFailedEvent> context) =>
        orderService.MarkConfirmationFailedAsync(context.Message.OrderId, context.Message.Reason, context.CancellationToken);
}
