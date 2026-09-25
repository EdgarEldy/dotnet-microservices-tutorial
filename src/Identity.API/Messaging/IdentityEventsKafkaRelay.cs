using Contracts;
using MassTransit;

namespace Identity.API.Messaging;

/// <summary>
/// The last hop of the transactional outbox, from the durable PostgreSQL bus queue to the Kafka
/// topics of <see cref="KafkaTopics"/>. Transport plumbing only, no business logic.
///
/// Why this hop exists (verified in the MassTransit 8.5.10 sources): the EF Core bus outbox
/// captures IPublishEndpoint.Publish in OutboxMessage, but its delivery service
/// (BusOutboxDeliveryService) relays rows to the bus transport through IBusControl.GetSendEndpoint.
/// Kafka is a rider, not a bus transport: an ITopicProducer is never intercepted by the outbox
/// (KafkaTopicSendTransportContext.CreateSendContext throws "Kafka is a producer, not an outbox
/// compatible transport") and a scoped producer outside a consume context produces straight to the
/// broker, before the database commit.
///
/// The path, at least once end to end, with no step where a crash loses the event:
/// 1. A service Publishes: the message is written to OutboxMessage in the business transaction.
/// 2. After commit, the delivery service sends it to this consumer's queue on MassTransit's
///    PostgreSQL transport (identity-db, "transport" schema), then deletes the outbox row.
/// 3. This consumer produces it to Kafka, keyed by user id so one user's events stay in order.
///    The SQL message is acknowledged (removed) only when Consume completes, i.e. after Kafka has
///    acknowledged the produce. A failed produce is retried, then redelivered from the queue.
/// A crash between two steps leaves the message in the outbox table or in the SQL queue, so it is
/// delivered again: duplicates are possible, losses are not. Consumers must be idempotent.
/// </summary>
public sealed class IdentityEventsKafkaRelay(
    ITopicProducer<UserRegisteredEvent> userRegisteredProducer,
    ITopicProducer<PasswordResetRequestedEvent> passwordResetRequestedProducer)
    : IConsumer<UserRegisteredEvent>, IConsumer<PasswordResetRequestedEvent>
{
    public Task Consume(ConsumeContext<UserRegisteredEvent> context) =>
        userRegisteredProducer.Produce(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<PasswordResetRequestedEvent> context) =>
        passwordResetRequestedProducer.Produce(context.Message, context.CancellationToken);
}
