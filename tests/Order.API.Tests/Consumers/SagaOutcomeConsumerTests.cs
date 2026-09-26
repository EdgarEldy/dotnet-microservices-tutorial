using System.Globalization;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Order.API.Data;
using Order.API.Messaging;
using Order.API.Models;
using Order.API.Tests.TestSupport;

namespace Order.API.Tests.Consumers;

/// <summary>
/// OrderConfirmedEventConsumer and NotificationFailedEventConsumer through the real Kafka rider:
/// each outcome event, produced on its notification-events topic the way notification-worker
/// produces it, moves a Pending order to its final Status, and a late outcome on an order that
/// already moved changes nothing.
/// </summary>
[Collection(OrderApiCollection.Name)]
public sealed class SagaOutcomeConsumerTests(OrderApiFixture fixture)
{
    [Fact]
    public async Task OrderConfirmedEventConsumer_ShouldMoveOrderToConfirmed_WhenOrderIsPending()
    {
        var orderId = await CreatePendingOrderAsync();

        await fixture.ProduceAsync(KafkaTopics.OrderConfirmed, Key(orderId), new OrderConfirmedEvent(orderId));

        await WaitForStatusAsync(orderId, OrderStatus.Confirmed);
    }

    [Fact]
    public async Task NotificationFailedEventConsumer_ShouldMoveOrderToConfirmationFailed_WhenOrderIsPending()
    {
        var orderId = await CreatePendingOrderAsync();

        await fixture.ProduceAsync(KafkaTopics.NotificationFailed, Key(orderId), new NotificationFailedEvent(orderId, "Simulated failure"));

        await WaitForStatusAsync(orderId, OrderStatus.ConfirmationFailed);
    }

    [Fact]
    public async Task NotificationFailedEventConsumer_ShouldLeaveStatusUnchanged_WhenOrderIsAlreadyConfirmed()
    {
        var orderId = await CreatePendingOrderAsync();
        await fixture.ProduceAsync(KafkaTopics.OrderConfirmed, Key(orderId), new OrderConfirmedEvent(orderId));
        await WaitForStatusAsync(orderId, OrderStatus.Confirmed);

        // The late outcome, then a witness on the same single-partition topic and with the same
        // Kafka key: MassTransit delivers messages sharing a key in order, so once the witness
        // order has moved, the late event has been consumed too.
        var witnessId = await CreatePendingOrderAsync();
        await fixture.ProduceAsync(KafkaTopics.NotificationFailed, Key(orderId), new NotificationFailedEvent(orderId, "Late outcome"));
        await fixture.ProduceAsync(KafkaTopics.NotificationFailed, Key(orderId), new NotificationFailedEvent(witnessId, "Witness"));
        await WaitForStatusAsync(witnessId, OrderStatus.ConfirmationFailed);

        Assert.Equal(OrderStatus.Confirmed, await GetStatusAsync(orderId));
    }

    [Fact]
    public async Task OrderConfirmedEventConsumer_ShouldLeaveStatusUnchanged_WhenOrderAlreadyFailed()
    {
        var orderId = await CreatePendingOrderAsync();
        await fixture.ProduceAsync(KafkaTopics.NotificationFailed, Key(orderId), new NotificationFailedEvent(orderId, "Simulated failure"));
        await WaitForStatusAsync(orderId, OrderStatus.ConfirmationFailed);

        var witnessId = await CreatePendingOrderAsync();
        await fixture.ProduceAsync(KafkaTopics.OrderConfirmed, Key(orderId), new OrderConfirmedEvent(orderId));
        await fixture.ProduceAsync(KafkaTopics.OrderConfirmed, Key(orderId), new OrderConfirmedEvent(witnessId));
        await WaitForStatusAsync(witnessId, OrderStatus.Confirmed);

        Assert.Equal(OrderStatus.ConfirmationFailed, await GetStatusAsync(orderId));
    }

    private static string Key(int orderId) => orderId.ToString(CultureInfo.InvariantCulture);

    /// <summary>A Pending order written straight to order-db (no HTTP call, so no OrderCreatedEvent).</summary>
    private async Task<int> CreatePendingOrderAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = new Order.API.Models.Order
        {
            UserId = OrderApiFixture.NewId(),
            CustomerId = OrderApiFixture.NewId(),
            ProductId = OrderApiFixture.NewId(),
            Quantity = 1,
            Total = 10m,
        };
        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OrderStatus.Pending, order.Status);
        return order.Id;
    }

    private Task<OrderStatus> GetStatusAsync(int orderId) =>
        fixture.QueryAsync(db => db.Orders.Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync());

    /// <summary>Polls order-db (bounded by the Kafka timeout) until the order reaches <paramref name="expected"/>.</summary>
    private async Task WaitForStatusAsync(int orderId, OrderStatus expected)
    {
        var deadline = TimeProvider.System.GetUtcNow() + OrderApiFixture.KafkaTimeout;
        var status = await GetStatusAsync(orderId);
        while (status != expected && TimeProvider.System.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
            status = await GetStatusAsync(orderId);
        }

        Assert.Equal(expected, status);
    }
}
