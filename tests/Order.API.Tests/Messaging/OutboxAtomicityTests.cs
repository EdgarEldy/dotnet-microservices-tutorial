using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Order.API.Dtos;
using Order.API.Tests.TestSupport;

namespace Order.API.Tests.Messaging;

/// <summary>
/// The README's outbox requirement, against real PostgreSQL and Kafka: OrderCreatedEvent is
/// written to OutboxMessage inside the same transaction as the order and its idempotency key
/// (visible from inside that transaction, invisible from outside), vanishes with them on rollback,
/// and reaches order-events.order-created only once that transaction has committed. The
/// CommitGateInterceptor stops the targeted transaction right before its commit; the marker is
/// both the customer e-mail (in the event) and the Idempotency-Key (next to the order).
/// </summary>
[Collection(OrderApiCollection.Name)]
public sealed class OutboxAtomicityTests(OrderApiFixture fixture)
{
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task CreateOrder_ShouldPersistNeitherOrderNorKeyNorOutboxMessage_WhenTransactionFailsAfterPublish()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, marker, request) = Arrange("outbox-rollback");
        var scenario = fixture.CommitGate.Arm(marker, Sql.OrdersWithIdempotencyKey, CommitAction.Fail);
        try
        {
            var response = await PostAsync(client, marker, request, ct);

            // Inside the transaction, just before the (failed) commit: order, key and event side by side.
            var snapshot = await scenario.Reached.Task.WaitAsync(CommitTimeout, ct);
            Assert.Equal(new InTransactionSnapshot(OutboxRows: 1, BusinessRows: 1), snapshot);

            await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.InternalServerError);
        }
        finally
        {
            fixture.CommitGate.Disarm(scenario);
        }

        var customerId = request.CustomerId.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(0, await fixture.CountCommittedAsync(Sql.OrdersForCustomer, customerId));
        Assert.Equal(0, await fixture.CountCommittedAsync(Sql.IdempotencyKeys, marker));
        Assert.Equal(0, await fixture.CountCommittedAsync(Sql.OutboxRowsForEmail, marker));

        // Nothing for it waits in the outbox or the SQL transport, and nothing reached the topic.
        await fixture.WaitUntilRelayedAsync(marker);
        Assert.Equal(0, await fixture.CountOrderCreatedEventsAsync(marker));
    }

    [Fact]
    public async Task CreateOrder_ShouldRelayOrderCreatedEventToKafkaOnlyAfterCommit_WhenOrderIsCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, marker, request) = Arrange("outbox-commit");
        var scenario = fixture.CommitGate.Arm(marker, Sql.OrdersWithIdempotencyKey, CommitAction.Hold);
        HttpResponseMessage response;
        try
        {
            var pending = PostAsync(client, marker, request, ct);

            // Commit held: the order, its key and its OutboxMessage exist inside the transaction...
            var snapshot = await scenario.Reached.Task.WaitAsync(CommitTimeout, ct);
            Assert.Equal(new InTransactionSnapshot(OutboxRows: 1, BusinessRows: 1), snapshot);

            // ...but not outside it, and nothing reached Kafka.
            Assert.Equal(0, await fixture.CountCommittedAsync(Sql.OrdersWithIdempotencyKey, marker));
            Assert.Equal(0, await fixture.CountCommittedAsync(Sql.OutboxRowsForEmail, marker));
            Assert.Equal(0, await fixture.CountOrderCreatedEventsAsync(marker));

            scenario.Release.SetResult();
            response = await pending.WaitAsync(CommitTimeout, ct);
        }
        finally
        {
            fixture.CommitGate.Disarm(scenario);
        }

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(ct);
        Assert.NotNull(order);
        Assert.Equal(1, await fixture.CountCommittedAsync(Sql.OrdersWithIdempotencyKey, marker));

        // After the commit, the relay delivers the event, carrying the committed order.
        var afterCommit = await fixture.ReadOrderCreatedEventAsync(marker);
        Assert.NotNull(afterCommit.Match);
        Assert.Equal(order.Id, KafkaTopicReader.GetInt32(afterCommit.Match, "orderId"));
        Assert.Equal("Outbox Book", KafkaTopicReader.GetString(afterCommit.Match, "productName"));
        Assert.Equal("Ada Lovelace", KafkaTopicReader.GetString(afterCommit.Match, "customerName"));
    }

    private (HttpClient Client, string Marker, CreateOrderRequest Request) Arrange(string prefix)
    {
        var userId = OrderApiFixture.NewId();
        var customerId = OrderApiFixture.NewId();
        var productId = OrderApiFixture.NewId();
        var marker = OrderApiFixture.NewMarker(prefix);
        fixture.StubProduct(productId, 9.99m, "Outbox Book");
        fixture.StubCustomer(customerId, userId, marker);
        return (fixture.CreateClientForUser(userId), marker, new CreateOrderRequest(customerId, productId, 2));
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string idempotencyKey, CreateOrderRequest request, CancellationToken ct)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/Orders") { Content = JsonContent.Create(request) };
        message.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(message, ct);
    }
}
