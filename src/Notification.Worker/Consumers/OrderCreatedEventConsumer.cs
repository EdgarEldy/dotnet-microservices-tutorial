using Contracts;
using MassTransit;
using Notification.Worker.Services;

namespace Notification.Worker.Consumers;

/// <summary>
/// Entry point of notification-worker's half of the choreographed order Saga: hands the
/// OrderCreatedEvent to <see cref="IOrderNotificationService"/>, which sends the e-mail and
/// produces the outcome (see OrderNotificationService for the delivery guarantees).
/// </summary>
public sealed class OrderCreatedEventConsumer(IOrderNotificationService orderNotificationService)
    : IConsumer<OrderCreatedEvent>
{
    public Task Consume(ConsumeContext<OrderCreatedEvent> context) =>
        orderNotificationService.ConfirmOrderAsync(context.Message, context.CancellationToken);
}
