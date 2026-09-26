using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Order.API.Dtos;
using Order.API.Tests.TestSupport;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Order.API.Tests.Controllers;

/// <summary>
/// /api/v1/Orders through the real pipeline, with catalog-api and customer-api stubbed by
/// WireMock.Net behind service discovery: synchronous validation (success, a missing resource as a
/// 422, an unavailable service as a 503 ProblemDetails), the caller's token forwarded, idempotent creation, owner-scoped reads
/// and the API Composition of GET by id.
/// </summary>
[Collection(OrderApiCollection.Name)]
public sealed class OrdersControllerTests(OrderApiFixture fixture)
{
    private const string OrdersUrl = "/api/v1/Orders";
    private const string ProductName = "Domain-Driven Design";
    private const decimal UnitPrice = 12.50m;

    [Fact]
    public async Task CreateOrder_ShouldReturnCreatedPendingOrderWithComputedTotal_WhenProductAndCustomerExist()
    {
        var ct = TestContext.Current.CancellationToken;
        var arranged = Arrange("create-ok");
        var token = fixture.CreateToken(arranged.UserId, OrderApiFixture.UserRole, OrderApiFixture.ReadPermission, OrderApiFixture.WritePermission);
        using var client = fixture.CreateClientWithToken(token);

        var response = await PostAsync(client, arranged.Marker, new CreateOrderRequest(arranged.CustomerId, arranged.ProductId, 3), ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(ct);
        Assert.NotNull(order);
        Assert.Equal(new OrderResponse(order.Id, arranged.CustomerId, arranged.ProductId, 3, 37.50m, "Pending"), order);
        Assert.EndsWith($"{OrdersUrl}/{order.Id}", response.Headers.Location?.ToString(), StringComparison.Ordinal);

        // The caller's own token reached both downstream services, exactly once per request.
        AssertForwardedToken(fixture.Catalog, OrderApiFixture.ProductPath(arranged.ProductId), token);
        AssertForwardedToken(fixture.Customers, OrderApiFixture.CustomerPath(arranged.CustomerId), token);
    }

    [Fact]
    public async Task CreateOrder_ShouldReturnUnprocessableEntity_WhenProductDoesNotExist()
    {
        var ct = TestContext.Current.CancellationToken;
        var arranged = Arrange("product-404");
        OrderApiFixture.StubStatus(fixture.Catalog, OrderApiFixture.ProductPath(arranged.ProductId), 404, arranged.ProductMapping);
        using var client = fixture.CreateClientForUser(arranged.UserId);

        var response = await PostAsync(client, arranged.Marker, new CreateOrderRequest(arranged.CustomerId, arranged.ProductId, 1), ct);

        var problem = await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Contains(arranged.ProductId.ToString(CultureInfo.InvariantCulture), problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountCommittedAsync(Sql.OrdersForCustomer, arranged.CustomerIdText));
    }

    [Fact]
    public async Task CreateOrder_ShouldReturnUnprocessableEntity_WhenCustomerDoesNotExist()
    {
        var ct = TestContext.Current.CancellationToken;
        var arranged = Arrange("customer-404", stubCustomer: false);
        OrderApiFixture.StubStatus(fixture.Customers, OrderApiFixture.CustomerPath(arranged.CustomerId), 404);
        using var client = fixture.CreateClientForUser(arranged.UserId);

        var response = await PostAsync(client, arranged.Marker, new CreateOrderRequest(arranged.CustomerId, arranged.ProductId, 1), ct);

        var problem = await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Contains(arranged.CustomerId.ToString(CultureInfo.InvariantCulture), problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountCommittedAsync(Sql.OrdersForCustomer, arranged.CustomerIdText));
    }

    [Theory]
    [InlineData("catalog-api")]
    [InlineData("customer-api")]
    public async Task CreateOrder_ShouldReturnServiceUnavailable_WhenDownstreamServiceReturnsServerError(string failingService)
    {
        var ct = TestContext.Current.CancellationToken;
        var arranged = Arrange("downstream-500", stubCustomer: failingService != "customer-api");
        if (failingService == "catalog-api")
        {
            OrderApiFixture.StubStatus(fixture.Catalog, OrderApiFixture.ProductPath(arranged.ProductId), 500, arranged.ProductMapping);
        }
        else
        {
            OrderApiFixture.StubStatus(fixture.Customers, OrderApiFixture.CustomerPath(arranged.CustomerId), 500);
        }

        using var client = fixture.CreateClientForUser(arranged.UserId);

        var response = await PostAsync(client, arranged.Marker, new CreateOrderRequest(arranged.CustomerId, arranged.ProductId, 1), ct);

        // Still failing after the retry: never a raw exception or a 500, but a 503 naming the
        // unavailable service, and nothing persisted.
        var problem = await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
        var detail = problem.GetProperty("detail").GetString();
        var serviceName = failingService == "catalog-api" ? "product service" : "customer service";
        Assert.Contains($"{serviceName} is unavailable", detail, StringComparison.Ordinal);
        Assert.Contains("500", detail, StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountCommittedAsync(Sql.OrdersForCustomer, arranged.CustomerIdText));
    }

    [Fact]
    public async Task CreateOrder_ShouldReturnServiceUnavailable_WhenCatalogTimesOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var arranged = Arrange("create-timeout");
        fixture.Catalog.Given(Request.Create().WithPath(OrderApiFixture.ProductPath(arranged.ProductId)).UsingGet())
            .WithGuid(arranged.ProductMapping)
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(OrderApiFixture.AttemptTimeout * 3));
        using var client = fixture.CreateClientForUser(arranged.UserId);

        var response = await PostAsync(client, arranged.Marker, new CreateOrderRequest(arranged.CustomerId, arranged.ProductId, 1), ct);

        // A downstream timeout ends in a 503 naming the unavailable service, never a hang or a 500.
        var problem = await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
        Assert.Contains("product service is unavailable (timed out)", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountCommittedAsync(Sql.OrdersForCustomer, arranged.CustomerIdText));
    }

    [Fact]
    public async Task CreateOrder_ShouldForwardExactlyOneAuthorizationValueOnRetry_WhenCatalogFailsOnceThenRecovers()
    {
        var ct = TestContext.Current.CancellationToken;
        var arranged = Arrange("retry-503", stubProduct: false);
        var path = OrderApiFixture.ProductPath(arranged.ProductId);
        var scenario = $"flaky-{arranged.ProductId}";
        fixture.Catalog.Given(Request.Create().WithPath(path).UsingGet())
            .InScenario(scenario)
            .WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(503));
        fixture.Catalog.Given(Request.Create().WithPath(path).UsingGet())
            .InScenario(scenario)
            .WhenStateIs("recovered")
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = arranged.ProductId, categoryId = 1, categoryName = "Books", productName = ProductName, unitPrice = UnitPrice,
            }));
        var token = fixture.CreateToken(arranged.UserId, OrderApiFixture.UserRole, OrderApiFixture.ReadPermission, OrderApiFixture.WritePermission);
        using var client = fixture.CreateClientWithToken(token);

        var response = await PostAsync(client, arranged.Marker, new CreateOrderRequest(arranged.CustomerId, arranged.ProductId, 2), ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var calls = fixture.Catalog.FindLogEntries(Request.Create().WithPath(path).UsingGet());
        Assert.Equal(2, calls.Count);
        AssertForwardedToken(fixture.Catalog, path, token);
    }

    [Fact]
    public async Task CreateOrder_ShouldReturnSameOrderAndPublishOneEvent_WhenIdempotencyKeyIsRepeated()
    {
        var ct = TestContext.Current.CancellationToken;
        var arranged = Arrange("idempotent");
        using var client = fixture.CreateClientForUser(arranged.UserId);
        var request = new CreateOrderRequest(arranged.CustomerId, arranged.ProductId, 2);

        var first = await PostAsync(client, arranged.Marker, request, ct);
        var second = await PostAsync(client, arranged.Marker, request, ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var firstOrder = await first.Content.ReadFromJsonAsync<OrderResponse>(ct);
        var secondOrder = await second.Content.ReadFromJsonAsync<OrderResponse>(ct);
        Assert.Equal(firstOrder, secondOrder);
        Assert.Equal(first.Headers.Location, second.Headers.Location);

        Assert.Equal(1, await fixture.CountCommittedAsync(Sql.OrdersForCustomer, arranged.CustomerIdText));
        Assert.Equal(1, await fixture.CountCommittedAsync(Sql.IdempotencyKeys, arranged.Marker));

        // One event: once it is on the topic and nothing for this e-mail waits in the outbox or the
        // SQL transport, reading the whole topic gives the final count.
        Assert.NotNull((await fixture.ReadOrderCreatedEventAsync(arranged.Marker)).Match);
        await fixture.WaitUntilRelayedAsync(arranged.Marker);
        Assert.Equal(1, await fixture.CountOrderCreatedEventsAsync(arranged.Marker));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task CreateOrder_ShouldReturnValidationProblem_WhenIdempotencyKeyHeaderIsMissing(string? key)
    {
        var ct = TestContext.Current.CancellationToken;
        var arranged = Arrange("no-key");
        using var client = fixture.CreateClientForUser(arranged.UserId);

        var response = await PostAsync(client, key, new CreateOrderRequest(arranged.CustomerId, arranged.ProductId, 1), ct);

        await ProblemAssertions.AssertValidationProblemAsync(response, "Idempotency-Key");
        Assert.Equal(0, await fixture.CountCommittedAsync(Sql.OrdersForCustomer, arranged.CustomerIdText));
    }

    [Fact]
    public async Task CreateOrder_ShouldReturnUnprocessableEntity_WhenIdempotencyKeyBelongsToAnotherUser()
    {
        var ct = TestContext.Current.CancellationToken;
        var arranged = Arrange("stolen-key");
        using var owner = fixture.CreateClientForUser(arranged.UserId);
        var created = await PostAsync(owner, arranged.Marker, new CreateOrderRequest(arranged.CustomerId, arranged.ProductId, 1), ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var otherUser = Arrange("stolen-key-other");
        using var other = fixture.CreateClientForUser(otherUser.UserId);

        var response = await PostAsync(other, arranged.Marker, new CreateOrderRequest(otherUser.CustomerId, otherUser.ProductId, 1), ct);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Equal(0, await fixture.CountCommittedAsync(Sql.OrdersForCustomer, otherUser.CustomerIdText));
        Assert.Equal(1, await fixture.CountCommittedAsync(Sql.OrdersForCustomer, arranged.CustomerIdText));
    }

    [Fact]
    public async Task GetOrder_ShouldReturnOrderWithProductAndCustomer_WhenCallerOwnsOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var arranged = Arrange("get-owner");
        using var client = fixture.CreateClientForUser(arranged.UserId);
        var order = await CreateOrderAsync(client, arranged, ct);

        var response = await client.GetAsync($"{OrdersUrl}/{order.Id}", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var details = await response.Content.ReadFromJsonAsync<OrderDetailsResponse>(ct);
        Assert.NotNull(details);
        Assert.Equal(order.Id, details.Id);
        Assert.Equal("Pending", details.Status);
        Assert.Equal(new Order.API.Clients.ProductDto(arranged.ProductId, 1, "Books", ProductName, UnitPrice), details.Product);
        Assert.Equal(
            new Order.API.Clients.CustomerDto(arranged.CustomerId, arranged.UserId, "Ada", "Lovelace", "+33600000000", arranged.Marker, "1 Test Street"),
            details.Customer);
    }

    [Fact]
    public async Task GetOrder_ShouldReturnNotFound_WhenOrderBelongsToAnotherUser()
    {
        var ct = TestContext.Current.CancellationToken;
        var arranged = Arrange("get-other");
        using var owner = fixture.CreateClientForUser(arranged.UserId);
        var order = await CreateOrderAsync(owner, arranged, ct);
        using var stranger = fixture.CreateClientForUser(OrderApiFixture.NewId());

        var response = await stranger.GetAsync($"{OrdersUrl}/{order.Id}", ct);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetOrder_ShouldReturnOrder_WhenCallerIsAdmin()
    {
        var ct = TestContext.Current.CancellationToken;
        var arranged = Arrange("get-admin");
        using var owner = fixture.CreateClientForUser(arranged.UserId);
        var order = await CreateOrderAsync(owner, arranged, ct);
        using var admin = fixture.CreateAdminClient(OrderApiFixture.NewId());

        var response = await admin.GetAsync($"{OrdersUrl}/{order.Id}", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var details = await response.Content.ReadFromJsonAsync<OrderDetailsResponse>(ct);
        Assert.Equal(order.Id, details?.Id);
    }

    [Fact]
    public async Task GetOrder_ShouldReturnOrderWithNullProduct_WhenCatalogReturnsServerError()
    {
        var ct = TestContext.Current.CancellationToken;
        var arranged = Arrange("get-catalog-500");
        using var client = fixture.CreateClientForUser(arranged.UserId);
        var order = await CreateOrderAsync(client, arranged, ct);
        OrderApiFixture.StubStatus(fixture.Catalog, OrderApiFixture.ProductPath(arranged.ProductId), 500, arranged.ProductMapping);

        var response = await client.GetAsync($"{OrdersUrl}/{order.Id}", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var details = await response.Content.ReadFromJsonAsync<OrderDetailsResponse>(ct);
        Assert.NotNull(details);
        Assert.Equal(order.Id, details.Id);
        Assert.Null(details.Product);
        Assert.NotNull(details.Customer);
    }

    [Fact]
    public async Task GetOrder_ShouldReturnOrderWithNullProduct_WhenCatalogTimesOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var arranged = Arrange("get-catalog-timeout");
        using var client = fixture.CreateClientForUser(arranged.UserId);
        var order = await CreateOrderAsync(client, arranged, ct);
        fixture.Catalog.Given(Request.Create().WithPath(OrderApiFixture.ProductPath(arranged.ProductId)).UsingGet())
            .WithGuid(arranged.ProductMapping)
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(OrderApiFixture.AttemptTimeout * 3));

        var response = await client.GetAsync($"{OrdersUrl}/{order.Id}", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var details = await response.Content.ReadFromJsonAsync<OrderDetailsResponse>(ct);
        Assert.NotNull(details);
        Assert.Null(details.Product);
        Assert.NotNull(details.Customer);
    }

    [Fact]
    public async Task GetOrders_ShouldReturnOnlyCallersOrdersWithTotalCount_WhenOtherUsersHaveOrders()
    {
        var ct = TestContext.Current.CancellationToken;
        var arranged = Arrange("list-mine");
        using var client = fixture.CreateClientForUser(arranged.UserId);
        var first = await CreateOrderAsync(client, arranged, ct);
        var second = await CreateOrderAsync(client, arranged with { Marker = OrderApiFixture.NewMarker("list-mine-2") }, ct);
        var otherUser = Arrange("list-other");
        using var other = fixture.CreateClientForUser(otherUser.UserId);
        await CreateOrderAsync(other, otherUser, ct);

        var response = await client.GetAsync(OrdersUrl, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2", Assert.Single(response.Headers.GetValues("X-Total-Count")));
        var orders = await response.Content.ReadFromJsonAsync<List<OrderResponse>>(ct);
        Assert.NotNull(orders);
        Assert.Equal([second.Id, first.Id], orders.Select(o => o.Id));
    }

    private sealed record Arranged(int UserId, int CustomerId, int ProductId, string Marker, Guid ProductMapping)
    {
        public string CustomerIdText => CustomerId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Fresh ids for one test; the product and customer are stubbed as existing unless told otherwise.</summary>
    private Arranged Arrange(string prefix, bool stubProduct = true, bool stubCustomer = true)
    {
        var arranged = new Arranged(
            OrderApiFixture.NewId(), OrderApiFixture.NewId(), OrderApiFixture.NewId(), OrderApiFixture.NewMarker(prefix), Guid.NewGuid());
        if (stubProduct)
        {
            fixture.StubProduct(arranged.ProductId, UnitPrice, ProductName, arranged.ProductMapping);
        }

        if (stubCustomer)
        {
            fixture.StubCustomer(arranged.CustomerId, arranged.UserId, arranged.Marker);
        }

        return arranged;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string? idempotencyKey, CreateOrderRequest request, CancellationToken ct)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, OrdersUrl) { Content = JsonContent.Create(request) };
        if (idempotencyKey is not null)
        {
            message.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return client.SendAsync(message, ct);
    }

    private static async Task<OrderResponse> CreateOrderAsync(HttpClient client, Arranged arranged, CancellationToken ct)
    {
        var response = await PostAsync(client, arranged.Marker, new CreateOrderRequest(arranged.CustomerId, arranged.ProductId, 1), ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderResponse>(ct))!;
    }

    /// <summary>Every request WireMock received on <paramref name="path"/> carried exactly the caller's bearer token.</summary>
    private static void AssertForwardedToken(WireMockServer server, string path, string token)
    {
        var entries = server.FindLogEntries(Request.Create().WithPath(path).UsingGet());
        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            var headers = entry.RequestMessage?.Headers;
            Assert.NotNull(headers);
            Assert.True(headers.TryGetValue("Authorization", out var values), $"No Authorization header on {path}.");
            Assert.Equal($"Bearer {token}", Assert.Single(values));
        }
    }
}
