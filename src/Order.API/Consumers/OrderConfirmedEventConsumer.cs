using Contracts;
using MassTransit;
using Order.API.Services;

namespace Order.API.Consumers;

/// <summary>
/// The Saga's success event, consumed from the notification-events.order-confirmed Kafka topic:
/// the only path that ever moves an order to Confirmed. Translates the message into a service call.
/// </summary>
public sealed class OrderConfirmedEventConsumer(IOrderService orderService) : IConsumer<OrderConfirmedEvent>
{
    public Task Consume(ConsumeContext<OrderConfirmedEvent> context) =>
        orderService.ConfirmAsync(context.Message.OrderId, context.CancellationToken);
}
