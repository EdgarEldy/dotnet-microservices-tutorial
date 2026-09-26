using System.Net;
using System.Text;
using ApiGateway.Tests.TestSupport;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace ApiGateway.Tests.Security;

/// <summary>
/// README (feature/api-gateway): the public identity endpoints pass through without a token, and
/// the downstream response (status, body, content type) comes back as identity-api wrote it.
/// </summary>
public sealed class PublicRoutePassThroughTests : IClassFixture<GatewayFactory>
{
    private readonly GatewayFactory _factory;

    public PublicRoutePassThroughTests(GatewayFactory factory)
    {
        _factory = factory;
        _factory.ResetDownstreams();
    }

    public static TheoryData<string, string> PublicRoutes => new()
    {
        { "POST", "/api/v1/Auth/Register" },
        { "POST", "/api/v1/Auth/Login" },
        { "GET", "/api/v1/Auth/ConfirmEmail?userId=42&token=abc" },
        { "POST", "/api/v1/Auth/Refresh" },
        { "POST", "/api/v1/Auth/ForgotPassword" },
        { "POST", "/api/v1/Auth/ResetPassword" },
    };

    [Theory]
    [MemberData(nameof(PublicRoutes))]
    public async Task InvokeAsync_ShouldReachIdentityApiWithUnmodifiedResponse_WhenPublicRouteIsCalledWithoutToken(
        string method, string pathAndQuery)
    {
        const string body = """{"accessToken":"a.b.c","refreshToken":"r","expiresIn":900}""";
        var path = pathAndQuery.Split('?')[0];
        _factory.Identity
            .Given(Request.Create().WithPath(path).UsingMethod(method))
            .AtPriority(1)
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json; charset=utf-8")
                .WithBody(body));
        var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), pathAndQuery);
        if (method == "POST")
        {
            request.Content = new StringContent("""{"email":"a@b.c"}""", Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(body, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var forwarded = Assert.Single(_factory.Identity.LogEntries).RequestMessage!;
        Assert.Equal(path, forwarded.Path);
        Assert.Equal(method, forwarded.Method);
        Assert.False(forwarded.Headers?.ContainsKey("Authorization") ?? false);
        if (method == "POST")
        {
            Assert.Equal("""{"email":"a@b.c"}""", forwarded.Body);
        }
        else
        {
            Assert.Equal("?userId=42&token=abc", forwarded.RawQuery);
        }

        foreach (var other in new[] { _factory.Catalog, _factory.Customer, _factory.Order })
        {
            Assert.Empty(other.LogEntries);
        }
    }

    [Theory]
    [InlineData("/api/v1/Auth/ForgotPassword", 404, """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"Not Found","status":404,"detail":"User 'a@b.c' was not found."}""")]
    [InlineData("/api/v1/Auth/Register", 422, """{"type":"https://tools.ietf.org/html/rfc4918#section-11.2","title":"Unprocessable Entity","status":422,"detail":"Email 'a@b.c' is already taken."}""")]
    [InlineData("/api/v1/Auth/Login", 400, """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Email":["'Email' must not be empty."]}}""")]
    public async Task InvokeAsync_ShouldReturnDownstreamProblemByteForByte_WhenIdentityApiAnswersWithError(
        string path, int status, string problem)
    {
        _factory.Identity
            .Given(Request.Create().WithPath(path).UsingPost())
            .AtPriority(1)
            .RespondWith(Response.Create()
                .WithStatusCode(status)
                .WithHeader("Content-Type", "application/problem+json")
                .WithBody(problem));
        var client = _factory.CreateClient();

        using var response = await client.PostAsync(
            path,
            new StringContent("""{"email":"a@b.c"}""", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(
            Encoding.UTF8.GetBytes(problem),
            await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Single(_factory.Identity.LogEntries);
    }

    [Fact]
    public async Task InvokeAsync_ShouldNotRewrapDownstreamError_WhenDownstreamErrorHasEmptyBody()
    {
        _factory.Identity
            .Given(Request.Create().WithPath("/api/v1/Auth/Refresh").UsingPost())
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound));
        var client = _factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/v1/Auth/Refresh",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Single(_factory.Identity.LogEntries);
    }
}
