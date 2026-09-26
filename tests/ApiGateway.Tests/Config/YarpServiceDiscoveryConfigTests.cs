using System.Net;
using System.Text;
using System.Text.Json;
using ApiGateway.Tests.TestSupport;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ApiGateway.Tests.Config;

/// <summary>
/// README (feature/api-gateway): each route reaches its service through the logical name
/// resolved by service discovery, here pointed at one WireMock server per service.
/// </summary>
public sealed class YarpServiceDiscoveryConfigTests : IClassFixture<GatewayFactory>
{
    private readonly GatewayFactory _factory;

    public YarpServiceDiscoveryConfigTests(GatewayFactory factory)
    {
        _factory = factory;
        _factory.ResetDownstreams();
    }

    public static TheoryData<string, string, string> Routes => new()
    {
        { "GET", "/api/v1/Auth/Me", "identity-api" },
        { "POST", "/api/v1/Auth/Logout", "identity-api" },
        { "GET", "/api/v1/Catalog/Products", "catalog-api" },
        { "GET", "/api/v1/Catalog/Products/7", "catalog-api" },
        { "POST", "/api/v1/Catalog/Categories", "catalog-api" },
        { "GET", "/api/v1/Customers", "customer-api" },
        { "POST", "/api/v1/Customers", "customer-api" },
        { "GET", "/api/v1/Customers/5", "customer-api" },
        { "PUT", "/api/v1/Customers/5", "customer-api" },
        { "GET", "/api/v1/Orders", "order-api" },
        { "POST", "/api/v1/Orders", "order-api" },
        { "GET", "/api/v1/Orders/12", "order-api" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task MapReverseProxy_ShouldForwardToOwningService_WhenPathMatchesItsRoute(
        string method, string path, string expectedService)
    {
        var client = _factory.CreateAuthenticatedClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expectedService, body.RootElement.GetProperty("service").GetString());

        var target = Server(expectedService);
        var forwarded = Assert.Single(target.LogEntries).RequestMessage!;
        Assert.Equal(path, forwarded.Path);
        Assert.Equal(method, forwarded.Method);
        foreach (var other in _factory.AllDownstreams.Where(s => s != target))
        {
            Assert.Empty(other.LogEntries);
        }
    }

    [Fact]
    public async Task MapReverseProxy_ShouldKeepQueryStringAndPaginationHeaders_WhenCatalogListIsProxied()
    {
        const string link = "</api/v1/Catalog/Products?page=2&size=10>; rel=\"next\"";
        _factory.Catalog
            .Given(Request.Create().WithPath("/api/v1/Catalog/Products").UsingGet())
            .AtPriority(1)
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("X-Total-Count", "25")
                .WithHeader("Link", link)
                .WithBody("[]"));
        var client = _factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/api/v1/Catalog/Products?page=1&size=10", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["25"], response.Headers.GetValues("X-Total-Count"));
        Assert.Equal([link], response.Headers.GetValues("Link"));
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("?page=1&size=10", Assert.Single(_factory.Catalog.LogEntries).RequestMessage!.RawQuery);
    }

    [Fact]
    public async Task MapReverseProxy_ShouldForwardClientHost_WhenRequestIsProxied()
    {
        // Services build absolute URLs (the Location of a 201) from the Host header: it must be
        // the gateway's public host, never the service's internal address.
        _factory.Order
            .Given(Request.Create().WithPath("/api/v1/Orders").UsingPost())
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Created));
        var client = _factory.CreateAuthenticatedClient();

        using var response = await client.PostAsync("/api/v1/Orders", new StringContent("{}"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var forwarded = Assert.Single(_factory.Order.LogEntries).RequestMessage!;
        Assert.Equal(client.BaseAddress!.Authority, Assert.Single(forwarded.Headers!["Host"]));
    }

    [Theory]
    [InlineData("/api/v1/Unknown")]
    [InlineData("/api/v2/Catalog/Products")]
    [InlineData("/")]
    public async Task MapReverseProxy_ShouldReturnNotFoundProblem_WhenNoRouteMatches(string path)
    {
        var client = _factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.NotFound);
        foreach (var server in _factory.AllDownstreams)
        {
            Assert.Empty(server.LogEntries);
        }
    }

    private WireMockServer Server(string serviceName) => serviceName switch
    {
        "identity-api" => _factory.Identity,
        "catalog-api" => _factory.Catalog,
        "customer-api" => _factory.Customer,
        _ => _factory.Order,
    };
}
