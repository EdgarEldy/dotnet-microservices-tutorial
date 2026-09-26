using System.Globalization;
using Confluent.Kafka;
using Contracts;
using MassTransit;
using Notification.Worker.Consumers;
using Notification.Worker.Messaging;
using Notification.Worker.Services;

namespace Notification.Worker.Extensions;

/// <summary>
/// Every registration of notification-worker, grouped here (the dotnet/eShop convention) so that
/// Program.cs stays short.
/// </summary>
public static class Extensions
{
    private const string KafkaConnectionName = "kafka";

    public static IHostApplicationBuilder AddApplicationServices(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        services.AddOptions<NotificationOptions>()
            .Bind(builder.Configuration.GetSection(NotificationOptions.SectionName));

        services.AddSingleton<IEmailNotification, EmailNotification>();

        builder.AddMessaging();

        return builder;
    }

    private static void AddMessaging(this IHostApplicationBuilder builder)
    {
        builder.Services.AddMassTransit(x =>
        {
            // MassTransit requires a bus even when every message goes through the Kafka rider.
            // In memory is enough: nothing is ever sent through it, and this service has no
            // database, hence no outbox to relay (see OrderCreatedEventConsumer).
            x.UsingInMemory();

            x.AddRider(rider =>
            {
                rider.AddConsumer<OrderCreatedEventConsumer>();
                rider.AddConsumer<UserRegisteredEventConsumer>();
                rider.AddConsumer<PasswordResetRequestedEventConsumer>();

                // Fail a produce after 30 s instead of librdkafka's 5 min default, so a Kafka
                // outage turns into a retry of the consumed message quickly.
                var producerConfig = new ProducerConfig { MessageTimeoutMs = 30_000 };

                // Keyed by order id, like order-events.order-created: one order's events stay in order.
                rider.AddProducer<string, OrderConfirmedEvent>(KafkaTopics.OrderConfirmed, producerConfig,
                    context => context.Message.OrderId.ToString(CultureInfo.InvariantCulture));
                rider.AddProducer<string, NotificationFailedEvent>(KafkaTopics.NotificationFailed, producerConfig,
                    context => context.Message.OrderId.ToString(CultureInfo.InvariantCulture));

                rider.UsingKafka((context, kafka) =>
                {
                    // Bootstrap servers injected by AppHost (WithReference(kafka)), read when the
                    // bus starts rather than at registration.
                    var connectionString = context.GetRequiredService<IConfiguration>().GetConnectionString(KafkaConnectionName)
                        ?? throw new InvalidOperationException(
                            $"Connection string '{KafkaConnectionName}' is missing: run notification-worker through AppHost.");
                    kafka.Host(connectionString);

                    kafka.TopicEndpoint<string, OrderCreatedEvent>(KafkaTopics.OrderCreated, KafkaTopics.ConsumerGroup, endpoint =>
                    {
                        endpoint.ConfigureTopic();
                        endpoint.ConfigureConsumer<OrderCreatedEventConsumer>(context);
                    });

                    kafka.TopicEndpoint<string, UserRegisteredEvent>(KafkaTopics.UserRegistered, KafkaTopics.ConsumerGroup, endpoint =>
                    {
                        endpoint.ConfigureTopic();
                        endpoint.ConfigureConsumer<UserRegisteredEventConsumer>(context);
                    });

                    kafka.TopicEndpoint<string, PasswordResetRequestedEvent>(KafkaTopics.PasswordResetRequested, KafkaTopics.ConsumerGroup, endpoint =>
                    {
                        endpoint.ConfigureTopic();
                        endpoint.ConfigureConsumer<PasswordResetRequestedEventConsumer>(context);
                    });
                });
            });
        });
    }

    /// <summary>
    /// The settings shared by every consumed topic: created if missing, read from the beginning
    /// on the first start of the consumer group, transient failures retried in process.
    /// </summary>
    private static void ConfigureTopic<TKey, TValue>(
        this IKafkaTopicReceiveEndpointConfigurator<TKey, TValue> endpoint)
        where TValue : class
    {
        endpoint.AutoOffsetReset = AutoOffsetReset.Earliest;
        endpoint.CreateIfMissing(topic => topic.NumPartitions = 1);
        endpoint.UseMessageRetry(retry => retry.Intervals(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)));
    }
}
