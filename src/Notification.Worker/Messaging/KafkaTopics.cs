namespace Notification.Worker.Messaging;

/// <summary>
/// The Kafka topics notification-worker consumes and produces. The README's "order-events",
/// "notification-events" and "identity-events" are split into one topic per event type, because
/// MassTransit 8.5.10 binds a Kafka topic to a single (key, value) type. The names are shared
/// with identity-api and order-api and must stay identical on both sides.
/// </summary>
public static class KafkaTopics
{
    public const string OrderCreated = "order-events.order-created";

    public const string OrderConfirmed = "notification-events.order-confirmed";

    public const string NotificationFailed = "notification-events.notification-failed";

    public const string UserRegistered = "identity-events.user-registered";

    public const string PasswordResetRequested = "identity-events.password-reset-requested";

    /// <summary>The consumer group of every topic this service reads.</summary>
    public const string ConsumerGroup = "notification-worker";
}
