namespace Order.API.Messaging;

/// <summary>
/// The Saga's Kafka topics, one per event type (MassTransit 8.5.10 binds a topic to a single
/// message type): the README's "order-events" and "notification-events", split by event.
/// notification-worker uses the same names.
/// </summary>
public static class KafkaTopics
{
    /// <summary>Produced by order-api, consumed by notification-worker.</summary>
    public const string OrderCreated = "order-events.order-created";

    /// <summary>Produced by notification-worker, consumed by order-api.</summary>
    public const string OrderConfirmed = "notification-events.order-confirmed";

    /// <summary>Produced by notification-worker, consumed by order-api.</summary>
    public const string NotificationFailed = "notification-events.notification-failed";

    /// <summary>Kafka consumer group of order-api's topic endpoints.</summary>
    public const string ConsumerGroup = "order-api";
}
