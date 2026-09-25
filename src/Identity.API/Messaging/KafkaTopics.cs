namespace Identity.API.Messaging;

/// <summary>
/// The README's "identity-events" topic, split into one topic per event type. MassTransit 8.5.10
/// binds a Kafka topic to a single (key, value) type on both sides: KafkaFactoryConfigurator
/// rejects a second producer for the same topic name ("A topic producer with the same key was
/// already added"), and a topic endpoint deserializes one value type. Both topics keep the
/// "identity-events" prefix and are consumed by notification-worker.
/// </summary>
public static class KafkaTopics
{
    public const string UserRegistered = "identity-events.user-registered";

    public const string PasswordResetRequested = "identity-events.password-reset-requested";
}
