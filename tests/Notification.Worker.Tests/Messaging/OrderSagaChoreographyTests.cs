using Contracts;
using Notification.Worker.Messaging;
using Notification.Worker.Tests.TestSupport;

namespace Notification.Worker.Tests.Messaging;

/// <summary>
/// notification-worker's half of the choreographed Saga, over a real Kafka broker: an
/// OrderCreatedEvent produced on order-events.order-created is answered with exactly one outcome,
/// OrderConfirmedEvent on the nominal path, NotificationFailedEvent when the e-mail fails. (The
/// order Status transitions are covered by order-api's own tests.)
/// </summary>
[Collection(KafkaCollection.Name)]
public sealed class OrderSagaChoreographyTests(NotificationWorkerFixture fixture)
{
    [Fact]
    public async Task Consume_ShouldProduceOrderConfirmedEventOnly_WhenOrderCreatedEventForRegularProductArrivesOnKafka()
    {
        var ct = TestContext.Current.CancellationToken;
        var order = NewOrder("Mechanical Keyboard");

        await fixture.Producer.ProduceAsync(order, ct);

        var confirmed = await KafkaTopicReader.ReadUntilAsync(
            fixture.KafkaBootstrapServers, KafkaTopics.OrderConfirmed, value => HasOrderId(value, order.OrderId),
            NotificationWorkerFixture.KafkaTimeout);
        Assert.NotNull(confirmed.Match);

        // Exactly one outcome: no NotificationFailedEvent for this order.
        var failures = await KafkaTopicReader.ReadToEndAsync(
            fixture.KafkaBootstrapServers, KafkaTopics.NotificationFailed, NotificationWorkerFixture.KafkaTimeout);
        Assert.DoesNotContain(failures, value => HasOrderId(value, order.OrderId));
    }

    [Fact]
    public async Task Consume_ShouldProduceNotificationFailedEventOnly_WhenOrderCreatedEventForFailingProductArrivesOnKafka()
    {
        var ct = TestContext.Current.CancellationToken;
        var order = NewOrder(NotificationWorkerFixture.FailingProductName);

        await fixture.Producer.ProduceAsync(order, ct);

        var failed = await KafkaTopicReader.ReadUntilAsync(
            fixture.KafkaBootstrapServers, KafkaTopics.NotificationFailed, value => HasOrderId(value, order.OrderId),
            NotificationWorkerFixture.KafkaTimeout);
        Assert.NotNull(failed.Match);
        Assert.False(string.IsNullOrWhiteSpace(KafkaTopicReader.GetString(failed.Match, "reason")));

        // The compensating outcome replaces the nominal one: an OrderConfirmedEvent is never produced as well.
        var confirmations = await KafkaTopicReader.ReadToEndAsync(
            fixture.KafkaBootstrapServers, KafkaTopics.OrderConfirmed, NotificationWorkerFixture.KafkaTimeout);
        Assert.DoesNotContain(confirmations, value => HasOrderId(value, order.OrderId));
    }

    private static OrderCreatedEvent NewOrder(string productName) =>
        new(NotificationWorkerFixture.NewId(), 7, "jane.doe@example.com", "Jane Doe", 42, productName, 2, 59.90m);

    private static bool HasOrderId(string value, int orderId) =>
        KafkaTopicReader.GetInt32(value, "orderId") == orderId;
}
