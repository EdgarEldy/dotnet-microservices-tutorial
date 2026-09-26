using System.Globalization;
using Contracts;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Notification.Worker.Messaging;

namespace Notification.Worker.Tests.TestSupport;

/// <summary>
/// A standalone MassTransit Kafka rider, in its own host, producing the events notification-worker
/// consumes with the same serializer (MassTransit's JSON envelope) and string keys as order-api
/// and identity-api, so what the worker reads is what the real publishers write.
/// </summary>
public sealed class KafkaEventProducer : IAsyncDisposable
{
    private readonly IHost host;

    private KafkaEventProducer(IHost host) => this.host = host;

    public static async Task<KafkaEventProducer> StartAsync(string bootstrapServers)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddMassTransit(x =>
        {
            x.UsingInMemory();
            x.AddRider(rider =>
            {
                rider.AddProducer<string, OrderCreatedEvent>(KafkaTopics.OrderCreated,
                    context => context.Message.OrderId.ToString(CultureInfo.InvariantCulture));
                rider.AddProducer<string, UserRegisteredEvent>(KafkaTopics.UserRegistered,
                    context => context.Message.UserId.ToString(CultureInfo.InvariantCulture));
                rider.AddProducer<string, PasswordResetRequestedEvent>(KafkaTopics.PasswordResetRequested,
                    context => context.Message.UserId.ToString(CultureInfo.InvariantCulture));

                rider.UsingKafka((_, kafka) => kafka.Host(bootstrapServers));
            });
        });

        var host = builder.Build();
        await host.StartAsync();
        return new KafkaEventProducer(host);
    }

    public async Task ProduceAsync<T>(T message, CancellationToken cancellationToken)
        where T : class
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ITopicProducer<T>>().Produce(message, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync();
        host.Dispose();
    }
}
