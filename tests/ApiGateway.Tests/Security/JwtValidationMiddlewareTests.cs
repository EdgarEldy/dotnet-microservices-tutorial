using System.Net;
using System.Net.Http.Headers;
using ApiGateway.Tests.TestSupport;

namespace ApiGateway.Tests.Security;

/// <summary>
/// README (feature/api-gateway): the gateway validates the JWT's signature and expiration on
/// every route except the public identity endpoints, before any downstream service is called.
/// </summary>
public sealed class JwtValidationMiddlewareTests : IClassFixture<GatewayFactory>
{
    private readonly GatewayFactory _factory;

    public JwtValidationMiddlewareTests(GatewayFactory factory)
    {
        _factory = factory;
        _factory.ResetDownstreams();
    }

    public static TheoryData<string, string> ProtectedRoutes => new()
    {
        { "GET", "/api/v1/Catalog/Products" },
        { "GET", "/api/v1/Customers" },
        { "GET", "/api/v1/Customers/5" },
        { "GET", "/api/v1/Orders" },
        { "POST", "/api/v1/Auth/Logout" },
        { "GET", "/api/v1/Auth/Me" },
    };

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task InvokeAsync_ShouldReturnUnauthorizedProblemWithoutCallingDownstream_WhenNoTokenIsSent(
        string method, string path)
    {
        var client = _factory.CreateClient();

        using var response = await client.SendAsync(
            new HttpRequestMessage(new HttpMethod(method), path), TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.Unauthorized);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        AssertNoDownstreamWasCalled();
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("other-key")]
    [InlineData("other-audience")]
    [InlineData("malformed")]
    public async Task InvokeAsync_ShouldReturnUnauthorizedProblemWithoutCallingDownstream_WhenTokenIsInvalid(string kind)
    {
        var token = kind switch
        {
            "expired" => TestTokens.CreateExpired(_factory.Services),
            "other-key" => TestTokens.CreateSignedWithOtherKey(_factory.Services),
            "other-audience" => TestTokens.CreateForOtherAudience(_factory.Services),
            _ => "not-a-jwt",
        };
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.GetAsync("/api/v1/Catalog/Products", TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.Unauthorized);
        AssertNoDownstreamWasCalled();
    }

    [Fact]
    public async Task InvokeAsync_ShouldForwardRequestWithSameAuthorizationHeader_WhenTokenIsValid()
    {
        var token = TestTokens.CreateValid(_factory.Services);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.GetAsync("/api/v1/Catalog/Products", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.Single(_factory.Catalog.LogEntries).RequestMessage!;
        Assert.Equal("/api/v1/Catalog/Products", request.Path);
        Assert.NotNull(request.Headers);
        Assert.Equal([$"Bearer {token}"], request.Headers["Authorization"].ToArray());
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturnOkWithoutToken_WhenLivenessEndpointIsCalled()
    {
        var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoDownstreamWasCalled();
    }

    private void AssertNoDownstreamWasCalled()
    {
        foreach (var server in _factory.AllDownstreams)
        {
            Assert.Empty(server.LogEntries);
        }
    }
}
