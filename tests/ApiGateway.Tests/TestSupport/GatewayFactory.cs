using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.Redis;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ApiGateway.Tests.TestSupport;

/// <summary>
/// The real api-gateway (Program, the whole pipeline) with its rate limiter counters in a Redis
/// container and one WireMock server per downstream service. Service discovery resolves each
/// logical name (https+http://catalog-api) to its WireMock server through the "services:*"
/// configuration keys AppHost would otherwise inject. Shared by the tests of one class.
/// </summary>
public class GatewayFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Login attempts allowed per client IP and window in the tests.</summary>
    public const int LoginPermitLimit = 3;

    /// <summary>
    /// Test-only header turned into the connection's remote IP address, since TestServer has no
    /// real client socket. A request without it keeps TestServer's default address.
    /// </summary>
    public const string ClientIpHeader = "X-Test-Client-Ip";

    private const string RedisImage = "redis:7.4";

    private readonly RedisContainer _redis = new RedisBuilder(RedisImage).Build();

    public WireMockServer Identity { get; } = WireMockServer.Start();

    public WireMockServer Catalog { get; } = WireMockServer.Start();

    public WireMockServer Customer { get; } = WireMockServer.Start();

    public WireMockServer Order { get; } = WireMockServer.Start();

    public IEnumerable<WireMockServer> AllDownstreams => [Identity, Catalog, Customer, Order];

    public async ValueTask InitializeAsync()
    {
        await _redis.StartAsync();
        ResetDownstreams();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        foreach (var server in AllDownstreams)
        {
            server.Stop();
            server.Dispose();
        }

        await _redis.DisposeAsync();
    }

    /// <summary>
    /// Clears every stub and request log, then makes each downstream answer any request with
    /// 200 and a JSON body naming the service, so a test can tell which one was reached.
    /// </summary>
    public void ResetDownstreams()
    {
        StubFallback(Identity, "identity-api");
        StubFallback(Catalog, "catalog-api");
        StubFallback(Customer, "customer-api");
        StubFallback(Order, "order-api");
    }

    /// <summary>A client carrying a valid access token.</summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestTokens.CreateValid(Services));
        return client;
    }

    /// <summary>
    /// Extra settings read while Program registers its services, such as
    /// ForwardedHeaders:TrustedProxies. None by default.
    /// </summary>
    protected virtual IEnumerable<KeyValuePair<string, string?>> AdditionalHostSettings => [];

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Read while Program registers its services (the Aspire Redis client integration), so it
        // must be host configuration; appsettings.json never sets it.
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:redis"] = _redis.GetConnectionString(),
        };
        foreach (var (key, value) in AdditionalHostSettings)
        {
            settings[key] = value;
        }

        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(settings));

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // App configuration comes after appsettings.json, so these values win over it.
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = TestTokens.SigningKey,
                ["Jwt:Issuer"] = "identity-api",
                ["Jwt:Audience"] = "dotnet-microservices-tutorial",
                ["RateLimiting:Login:PermitLimit"] = LoginPermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["RateLimiting:Login:WindowSeconds"] = "60",
                ["services:identity-api:http:0"] = Identity.Urls[0],
                ["services:catalog-api:http:0"] = Catalog.Urls[0],
                ["services:customer-api:http:0"] = Customer.Urls[0],
                ["services:order-api:http:0"] = Order.Urls[0],
            }));

        builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter, ClientIpStartupFilter>());
    }

    private static void StubFallback(WireMockServer server, string serviceName)
    {
        server.Reset();
        server
            .Given(Request.Create().WithPath("/*"))
            .AtPriority(100)
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"service":"{{serviceName}}"}"""));
    }

    private sealed class ClientIpStartupFilter : IStartupFilter
    {
        // A startup filter wraps the whole pipeline Program builds, so the address is the
        // connection's own before UseForwardedHeaders reads it, as a real socket's would be.
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (HttpContext context, RequestDelegate nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(ClientIpHeader, out var value)
                    && IPAddress.TryParse(value.ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
