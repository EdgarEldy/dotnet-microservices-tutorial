using System.Net;
using System.Text;
using ApiGateway.Tests.TestSupport;

namespace ApiGateway.Tests.Config;

/// <summary>
/// README (feature/api-gateway): a Redis-backed limiter on /api/v1/Auth/Login, a fixed number of
/// attempts per client IP; past it the gateway answers 429 without reaching identity-api.
/// Every test uses its own client IP, so the shared Redis counters never leak between tests.
/// </summary>
public sealed class RateLimiterPoliciesTests
    : IClassFixture<GatewayFactory>, IClassFixture<TrustedProxyGatewayFactory>
{
    private static int _nextClientIp;

    private readonly GatewayFactory _factory;
    private readonly TrustedProxyGatewayFactory _behindProxy;

    public RateLimiterPoliciesTests(GatewayFactory factory, TrustedProxyGatewayFactory behindProxy)
    {
        _factory = factory;
        _factory.ResetDownstreams();
        _behindProxy = behindProxy;
        _behindProxy.ResetDownstreams();
    }

    [Fact]
    public async Task Login_ShouldGiveEachForwardedClientItsOwnBudget_WhenRequestsComeFromTrustedProxy()
    {
        var firstClient = CreateClientFrom(_behindProxy, TrustedProxyGatewayFactory.ProxyIp, NewClientIp());
        var secondClient = CreateClientFrom(_behindProxy, TrustedProxyGatewayFactory.ProxyIp, NewClientIp());
        await ExhaustLoginBudgetAsync(firstClient);

        using var response = await PostAsync(secondClient, "/api/v1/Auth/Login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_ShouldShareOneBudget_WhenUntrustedClientVariesForwardedFor()
    {
        await AssertForwardedForCannotBypassLimitAsync(_factory);
    }

    [Fact]
    public async Task Login_ShouldShareOneBudget_WhenClientOtherThanTrustedProxyVariesForwardedFor()
    {
        await AssertForwardedForCannotBypassLimitAsync(_behindProxy);
    }

    // One connection address, a new X-Forwarded-For value on every attempt: the header is not
    // trusted from that address, so every attempt lands in the same bucket.
    private static async Task AssertForwardedForCannotBypassLimitAsync(GatewayFactory factory)
    {
        var clientIp = NewClientIp();

        for (var attempt = 0; attempt < GatewayFactory.LoginPermitLimit; attempt++)
        {
            using var allowed = await PostAsync(CreateClientFrom(factory, clientIp, NewClientIp()), "/api/v1/Auth/Login");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var rejected = await PostAsync(CreateClientFrom(factory, clientIp, NewClientIp()), "/api/v1/Auth/Login");

        await ProblemAssertions.AssertProblemAsync(rejected, HttpStatusCode.TooManyRequests);
        Assert.Equal(GatewayFactory.LoginPermitLimit, factory.Identity.LogEntries.Count());
    }

    private static HttpClient CreateClientFrom(GatewayFactory factory, string connectionIp, string forwardedFor)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(GatewayFactory.ClientIpHeader, connectionIp);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        return client;
    }

    [Fact]
    public async Task Login_ShouldReturnTooManyRequestsProblem_WhenPermitLimitIsExceeded()
    {
        var client = CreateClientFrom(NewClientIp());

        for (var attempt = 1; attempt <= GatewayFactory.LoginPermitLimit; attempt++)
        {
            using var allowed = await PostAsync(client, "/api/v1/Auth/Login");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var rejected = await PostAsync(client, "/api/v1/Auth/Login");

        await ProblemAssertions.AssertProblemAsync(rejected, HttpStatusCode.TooManyRequests);
        Assert.Equal(GatewayFactory.LoginPermitLimit, _factory.Identity.LogEntries.Count());
    }

    [Fact]
    public async Task Login_ShouldNotLimitOtherRoutes_WhenLoginBudgetIsExhausted()
    {
        var client = CreateClientFrom(NewClientIp());
        await ExhaustLoginBudgetAsync(client);

        for (var attempt = 0; attempt <= GatewayFactory.LoginPermitLimit; attempt++)
        {
            using var register = await PostAsync(client, "/api/v1/Auth/Register");
            Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        }
    }

    [Fact]
    public async Task Login_ShouldGiveEachClientIpItsOwnBudget_WhenAnotherIpExhaustedItsBudget()
    {
        await ExhaustLoginBudgetAsync(CreateClientFrom(NewClientIp()));
        var otherClient = CreateClientFrom(NewClientIp());

        using var response = await PostAsync(otherClient, "/api/v1/Auth/Login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task ExhaustLoginBudgetAsync(HttpClient client)
    {
        for (var attempt = 0; attempt < GatewayFactory.LoginPermitLimit; attempt++)
        {
            using var allowed = await PostAsync(client, "/api/v1/Auth/Login");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var rejected = await PostAsync(client, "/api/v1/Auth/Login");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    private HttpClient CreateClientFrom(string clientIp)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(GatewayFactory.ClientIpHeader, clientIp);
        return client;
    }

    private static string NewClientIp() => $"203.0.113.{Interlocked.Increment(ref _nextClientIp)}";

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path) =>
        client.PostAsync(
            path,
            new StringContent("""{"email":"a@b.c","password":"wrong"}""", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
}
