using System.Net;
using System.Text;
using ApiGateway.Tests.TestSupport;

namespace ApiGateway.Tests.Config;

/// <summary>
/// README (feature/api-gateway): a Redis-backed limiter on /api/v1/Auth/Login, a fixed number of
/// attempts per client IP; past it the gateway answers 429 without reaching identity-api.
/// Every test uses its own client IP, so the shared Redis counters never leak between tests.
/// </summary>
public sealed class RateLimiterPoliciesTests : IClassFixture<GatewayFactory>
{
    private static int _nextClientIp;

    private readonly GatewayFactory _factory;

    public RateLimiterPoliciesTests(GatewayFactory factory)
    {
        _factory = factory;
        _factory.ResetDownstreams();
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
