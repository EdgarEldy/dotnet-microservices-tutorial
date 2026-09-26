using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Order.API.ResilienceTests.TestSupport;

/// <summary>
/// The real order-api (Program, the real resilience pipeline, service discovery, exception
/// handlers) with catalog-api and customer-api replaced by two WireMock.Net servers, found through
/// service discovery exactly as AppHost wires them ("services:catalog-api:http:0"). One instance
/// per test: its circuit breakers start Closed. Resilience settings are short test values.
/// </summary>
public sealed class OrderApiFactory : WebApplicationFactory<Program>
{
    public const string OrderWritePermission = "ORDER:WRITE";
    public const string OrderReadPermission = "ORDER:READ";
    public const string AdminRole = "Admin";
    public const string CustomerRole = "Customer";

    public const int UserId = 4242;
    public const int CustomerId = 7;
    public const int ProductId = 11;

    public const string ProductPath = "/api/v1/Catalog/Products/11";
    public const string CustomerPath = "/api/v1/Customers/7";

    private readonly ContainersFixture _containers;
    private readonly Dictionary<string, string?> _settings;

    public OrderApiFactory(ContainersFixture containers, IDictionary<string, string?>? resilienceOverrides = null)
    {
        _containers = containers;

        _settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Resilience:Downstream:FailureRatio"] = "0.5",
            ["Resilience:Downstream:MinimumThroughput"] = "4",
            ["Resilience:Downstream:SamplingDuration"] = "00:00:30",
            ["Resilience:Downstream:BreakDuration"] = "00:00:30",
            ["Resilience:Downstream:RetryCount"] = "1",
            ["Resilience:Downstream:RetryDelay"] = "00:00:00.010",
            ["Resilience:Downstream:AttemptTimeout"] = "00:00:02",
            ["Resilience:Downstream:TotalTimeout"] = "00:00:05",
        };

        foreach (var (key, value) in resilienceOverrides ?? new Dictionary<string, string?>())
        {
            _settings[$"Resilience:Downstream:{key}"] = value;
        }
    }

    /// <summary>Stands in for catalog-api.</summary>
    public WireMockServer Catalog { get; } = WireMockServer.Start();

    /// <summary>Stands in for customer-api.</summary>
    public WireMockServer Customers { get; } = WireMockServer.Start();

    /// <summary>Every log entry the host wrote, structured state included.</summary>
    public FakeLogCollector Logs => Services.GetFakeLogCollector();

    /// <summary>A caller allowed to create orders (the customer profile it orders for is its own).</summary>
    public HttpClient CreateCustomerClient() =>
        CreateAuthorizedClient(CustomerRole, OrderWritePermission, OrderReadPermission);

    /// <summary>An Admin, the only role allowed to read the circuit breaker diagnostics.</summary>
    public HttpClient CreateAdminClient() =>
        CreateAuthorizedClient(AdminRole, OrderWritePermission, OrderReadPermission);

    public HttpClient CreateAuthorizedClient(string role, params string[] permissions)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Create(Services, UserId, role, permissions));
        return client;
    }

    /// <summary>catalog-api answers the product.</summary>
    public void CatalogReturnsProduct() =>
        Catalog.Given(Request.Create().WithPath(ProductPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = ProductId,
                categoryId = 1,
                categoryName = "Books",
                productName = "Domain-Driven Design",
                unitPrice = 42.50m,
            }));

    /// <summary>customer-api answers the caller's own profile.</summary>
    public void CustomersReturnCustomer() =>
        Customers.Given(Request.Create().WithPath(CustomerPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = CustomerId,
                userId = UserId,
                firstName = "Ada",
                lastName = "Lovelace",
                telephone = "+33600000000",
                email = "ada@example.com",
                address = "1 Analytical Engine Street",
            }));

    /// <summary>catalog-api answers every product request with the given status.</summary>
    public void CatalogAnswers(int statusCode) =>
        Catalog.Given(Request.Create().WithPath(ProductPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(statusCode));

    /// <summary>How many requests catalog-api actually received.</summary>
    public int CatalogRequestCount => Catalog.LogEntries.Count();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:order-db", _containers.DatabaseConnectionString);
        builder.UseSetting("ConnectionStrings:kafka", _containers.KafkaBootstrapServers);
        builder.UseSetting("Jwt:SigningKey", TestTokens.SigningKey);

        // Service discovery, the same keys AppHost injects for WithReference(catalogService).
        builder.UseSetting("services:catalog-api:http:0", Catalog.Url);
        builder.UseSetting("services:customer-api:http:0", Customers.Url);

        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging => logging.AddFakeLogging());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Catalog.Stop();
        Catalog.Dispose();
        Customers.Stop();
        Customers.Dispose();
    }
}
