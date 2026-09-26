using System.Net;
using Order.API.Clients;
using Order.API.ContractTests.TestSupport;
using PactNet;
using PactNet.Matchers;
using Refit;

namespace Order.API.ContractTests;

/// <summary>
/// order-api's expectations of catalog-api's GET /api/v1/Catalog/Products/{id}, recorded into
/// pacts/order-api-catalog-api.json. Every body field is matched on its type, never its value:
/// the contract is the shape ProductDto deserializes, not one particular product.
/// </summary>
public sealed class ProductClientPactTests
{
    public const string Provider = "catalog-api";

    private readonly IPactBuilderV4 _pact = OrderApiPact.For(Provider);

    [Fact]
    public async Task GetProductAsync_ShouldDeserializeTheProduct_WhenTheProductExists()
    {
        _pact
            .UponReceiving("a request for an existing product")
            .Given("a product with id 1 exists")
            .WithRequest(HttpMethod.Get, "/api/v1/Catalog/Products/1")
            .WithHeader("Authorization", Match.Regex(OrderApiPact.CallerAuthorization, OrderApiPact.BearerTokenRegex))
            .WillRespond()
            .WithStatus(HttpStatusCode.OK)
            .WithHeader("Content-Type", Match.Regex("application/json; charset=utf-8", OrderApiPact.JsonContentTypeRegex))
            .WithJsonBody(new
            {
                id = Match.Integer(1),
                categoryId = Match.Integer(1),
                categoryName = Match.Type("Beverages"),
                productName = Match.Type("Espresso"),
                unitPrice = Match.Number(2.5m),
            });

        await _pact.VerifyAsync(async context =>
        {
            var client = OrderApiPact.CreateClient<IProductClient>(context.MockServerUri);

            var product = await client.GetProductAsync(1, TestContext.Current.CancellationToken);

            Assert.Equal(new ProductDto(1, 1, "Beverages", "Espresso", 2.5m), product);
        });
    }

    [Fact]
    public async Task GetProductAsync_ShouldThrowNotFound_WhenTheProductDoesNotExist()
    {
        _pact
            .UponReceiving("a request for a product that does not exist")
            .Given("no product with id 999")
            .WithRequest(HttpMethod.Get, "/api/v1/Catalog/Products/999")
            .WithHeader("Authorization", Match.Regex(OrderApiPact.CallerAuthorization, OrderApiPact.BearerTokenRegex))
            .WillRespond()
            .WithStatus(HttpStatusCode.NotFound)
            .WithHeader("Content-Type", Match.Regex("application/problem+json; charset=utf-8", OrderApiPact.ProblemContentTypeRegex));

        await _pact.VerifyAsync(async context =>
        {
            var client = OrderApiPact.CreateClient<IProductClient>(context.MockServerUri);

            // OrderService turns exactly this (an ApiException with 404) into "product does not exist".
            var exception = await Assert.ThrowsAsync<ApiException>(
                () => client.GetProductAsync(999, TestContext.Current.CancellationToken));
            Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        });
    }
}
