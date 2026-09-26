using Contracts;
using MassTransit;

namespace Order.API.Messaging;

/// <summary>
/// The last hop of the transactional outbox, from the durable PostgreSQL bus queue to Kafka:
/// the same path as identity-api's relay. OrderService publishes OrderCreatedEvent before
/// SaveChangesAsync, so it lands in OutboxMessage in the order's own transaction; after commit the
/// outbox delivery service sends it to this consumer's queue (order-db, "transport" schema); this
/// consumer produces it to Kafka, keyed by order id, and the SQL message is acknowledged only once
/// Kafka acknowledged the produce. A crash leaves it in the outbox or the queue: at least once,
/// never lost, never published before the order exists. Transport plumbing only.
/// </summary>
public sealed class OrderEventsKafkaRelay(ITopicProducer<string, OrderCreatedEvent> orderCreatedProducer)
    : IConsumer<OrderCreatedEvent>
{
    public Task Consume(ConsumeContext<OrderCreatedEvent> context) =>
        orderCreatedProducer.Produce(
            context.Message.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            context.Message,
            context.CancellationToken);
}
