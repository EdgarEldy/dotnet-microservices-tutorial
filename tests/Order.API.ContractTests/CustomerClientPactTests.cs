using System.Net;
using Order.API.Clients;
using Order.API.ContractTests.TestSupport;
using PactNet;
using PactNet.Matchers;
using Refit;

namespace Order.API.ContractTests;

/// <summary>
/// order-api's expectations of customer-api's GET /api/v1/Customers/{id}, recorded into
/// pacts/order-api-customer-api.json. customer-api only returns the caller's own profile, which
/// is why the forwarded bearer token is part of the contract.
/// </summary>
public sealed class CustomerClientPactTests
{
    public const string Provider = "customer-api";

    private readonly IPactBuilderV4 _pact = OrderApiPact.For(Provider);

    [Fact]
    public async Task GetCustomerAsync_ShouldDeserializeTheCustomer_WhenTheCallerOwnsTheProfile()
    {
        _pact
            .UponReceiving("a request for the caller's own customer profile")
            .Given("a customer with id 1 exists")
            .WithRequest(HttpMethod.Get, "/api/v1/Customers/1")
            .WithHeader("Authorization", Match.Regex(OrderApiPact.CallerAuthorization, OrderApiPact.BearerTokenRegex))
            .WillRespond()
            .WithStatus(HttpStatusCode.OK)
            .WithHeader("Content-Type", Match.Regex("application/json; charset=utf-8", OrderApiPact.JsonContentTypeRegex))
            .WithJsonBody(new
            {
                id = Match.Integer(1),
                userId = Match.Integer(42),
                firstName = Match.Type("Ada"),
                lastName = Match.Type("Lovelace"),
                telephone = Match.Type("+33600000000"),
                email = Match.Type("ada@example.com"),
                address = Match.Type("1 Analytical Engine Street"),
            });

        await _pact.VerifyAsync(async context =>
        {
            var client = OrderApiPact.CreateClient<ICustomerClient>(context.MockServerUri);

            var customer = await client.GetCustomerAsync(1, TestContext.Current.CancellationToken);

            Assert.Equal(
                new CustomerDto(1, 42, "Ada", "Lovelace", "+33600000000", "ada@example.com", "1 Analytical Engine Street"),
                customer);
        });
    }

    [Fact]
    public async Task GetCustomerAsync_ShouldThrowNotFound_WhenTheCustomerDoesNotExist()
    {
        _pact
            .UponReceiving("a request for a customer that does not exist")
            .Given("no customer with id 999")
            .WithRequest(HttpMethod.Get, "/api/v1/Customers/999")
            .WithHeader("Authorization", Match.Regex(OrderApiPact.CallerAuthorization, OrderApiPact.BearerTokenRegex))
            .WillRespond()
            .WithStatus(HttpStatusCode.NotFound)
            .WithHeader("Content-Type", Match.Regex("application/problem+json; charset=utf-8", OrderApiPact.ProblemContentTypeRegex));

        await _pact.VerifyAsync(async context =>
        {
            var client = OrderApiPact.CreateClient<ICustomerClient>(context.MockServerUri);

            // OrderService turns exactly this (an ApiException with 404) into "customer does not exist".
            var exception = await Assert.ThrowsAsync<ApiException>(
                () => client.GetCustomerAsync(999, TestContext.Current.CancellationToken));
            Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        });
    }
}
