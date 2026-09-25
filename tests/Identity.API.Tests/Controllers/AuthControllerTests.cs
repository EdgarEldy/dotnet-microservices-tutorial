using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Identity.API.Dtos;
using Identity.API.Messaging;
using Identity.API.Security;
using Identity.API.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Identity.API.Tests.Controllers;

/// <summary>
/// /api/v1/Auth/* over HTTP, through the real pipeline (ValidationFilter, exception handlers,
/// JwtBearer with the blacklist check) against real PostgreSQL and Kafka. The confirmation token
/// is read from the UserRegisteredEvent that reached Kafka, as notification-worker would.
/// </summary>
public sealed class AuthControllerTests(IdentityApiFixture fixture) : IClassFixture<IdentityApiFixture>
{
    private const string ProblemJson = "application/problem+json";

    private readonly HttpClient client = fixture.CreateClient();

    [Fact]
    public async Task Register_ShouldReturnAcceptedWithNoBody_WhenEmailIsNew()
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/Auth/Register", new RegisterRequest(IdentityApiFixture.NewEmail("new"), IdentityApiFixture.ValidPassword),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Register_ShouldReturnSameAcceptedResponse_WhenEmailIsAlreadyRegistered()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = IdentityApiFixture.NewEmail("duplicate");
        var request = new RegisterRequest(email, IdentityApiFixture.ValidPassword);

        var first = await client.PostAsJsonAsync("/api/v1/Auth/Register", request, ct);
        var second = await client.PostAsJsonAsync("/api/v1/Auth/Register", request, ct);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(ct),
            await second.Content.ReadAsStringAsync(ct));
        Assert.Equal(first.Content.Headers.ContentType, second.Content.Headers.ContentType);
        Assert.Equal(1, await fixture.CountCommittedAsync(Sql.UsersWithEmail, email));
    }

    [Fact]
    public async Task Register_ShouldReturnValidationProblemDetails_WhenBodyIsInvalid()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await client.PostAsJsonAsync("/api/v1/Auth/Register", new RegisterRequest("not-an-email", "short"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(ct);
        Assert.NotNull(problem);
        Assert.Equal(400, problem.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
        Assert.Contains("Email", problem.Errors.Keys);
        Assert.Contains("Password", problem.Errors.Keys);
    }

    [Fact]
    public async Task ConfirmEmail_ShouldReturnValidationProblemDetails_WhenTokenIsInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await fixture.CreateConfirmedUserAsync(IdentityApiFixture.NewEmail("confirm-invalid"));

        var response = await client.GetAsync(
            $"/api/v1/Auth/ConfirmEmail?userId={user.Id}&token={TokenEncoding.Encode("forged-token")}", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(ct);
        Assert.NotNull(problem);
        Assert.Equal(400, problem.Status);
        Assert.Contains("Token", problem.Errors.Keys);
    }

    [Fact]
    public async Task Login_ShouldReturnUnauthorizedProblemDetails_WhenPasswordIsWrong()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = IdentityApiFixture.NewEmail("wrong-password");
        await fixture.CreateConfirmedUserAsync(email);

        var response = await client.PostAsJsonAsync("/api/v1/Auth/Login", new LoginRequest(email, "Wr0ng!Password"), ct);

        await AssertUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task Login_ShouldReturnSameUnauthorizedProblemDetails_WhenEmailIsUnknown()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = IdentityApiFixture.NewEmail("known");
        await fixture.CreateConfirmedUserAsync(email);

        var wrongPassword = await client.PostAsJsonAsync("/api/v1/Auth/Login", new LoginRequest(email, "Wr0ng!Password"), ct);
        var unknownEmail = await client.PostAsJsonAsync(
            "/api/v1/Auth/Login", new LoginRequest(IdentityApiFixture.NewEmail("unknown"), IdentityApiFixture.ValidPassword), ct);

        var wrongPasswordProblem = await AssertUnauthorizedProblemAsync(wrongPassword);
        var unknownEmailProblem = await AssertUnauthorizedProblemAsync(unknownEmail);
        Assert.Equal(wrongPasswordProblem.Title, unknownEmailProblem.Title);
        Assert.Equal(wrongPasswordProblem.Detail, unknownEmailProblem.Detail);
    }

    [Fact]
    public async Task Me_ShouldReturnUnauthorizedProblemDetails_WhenNoAccessTokenIsSent()
    {
        var response = await client.GetAsync("/api/v1/Auth/Me", TestContext.Current.CancellationToken);

        await AssertUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task Logout_ShouldRevokeAccessToken_WhenUserGoesThroughRegisterConfirmLoginRefresh()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = IdentityApiFixture.NewEmail("happy-path");

        // Register: 202, and the UserRegisteredEvent carrying the confirmation token reaches Kafka.
        var register = await client.PostAsJsonAsync("/api/v1/Auth/Register", new RegisterRequest(email, IdentityApiFixture.ValidPassword), ct);
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);

        var read = await KafkaTopicReader.ReadUntilAsync(
            fixture.KafkaBootstrapServers, KafkaTopics.UserRegistered,
            value => KafkaTopicReader.HasEmail(value, email), IdentityApiFixture.KafkaTimeout);
        Assert.NotNull(read.Match);
        var userId = KafkaTopicReader.GetInt32(read.Match, "userId");
        var confirmationToken = KafkaTopicReader.GetString(read.Match, "confirmationToken");
        Assert.False(string.IsNullOrEmpty(confirmationToken));

        // Login before confirmation is refused.
        var loginRequest = new LoginRequest(email, IdentityApiFixture.ValidPassword);
        await AssertUnauthorizedProblemAsync(await client.PostAsJsonAsync("/api/v1/Auth/Login", loginRequest, ct));

        // ConfirmEmail: 204.
        var confirm = await client.GetAsync($"/api/v1/Auth/ConfirmEmail?userId={userId}&token={confirmationToken}", ct);
        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);

        // Login: 200 with the raw TokenResponse, no envelope, permissions embedded in the JWT.
        var login = await client.PostAsJsonAsync("/api/v1/Auth/Login", loginRequest, ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal("application/json", login.Content.Headers.ContentType?.MediaType);
        var tokens = await login.Content.ReadFromJsonAsync<TokenResponse>(ct);
        Assert.NotNull(tokens);
        Assert.Equal("Bearer", tokens.TokenType);
        Assert.False(string.IsNullOrEmpty(tokens.RefreshToken));

        string[] userPermissions = [
            AppPermissions.CustomerRead, AppPermissions.CustomerWrite, AppPermissions.OrderRead, AppPermissions.OrderWrite];
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(tokens.AccessToken);
        Assert.Equal(userId.ToString(System.Globalization.CultureInfo.InvariantCulture), jwt.Subject);
        Assert.Equal(userPermissions, jwt.Claims.Where(c => c.Type == AppClaimTypes.Permission).Select(c => c.Value).Order(StringComparer.Ordinal));

        // Me: 200 with roles and permissions.
        var me = await SendWithTokenAsync(HttpMethod.Get, "/api/v1/Auth/Me", tokens.AccessToken, ct);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var profile = await me.Content.ReadFromJsonAsync<UserProfileResponse>(ct);
        Assert.NotNull(profile);
        Assert.Equal(userId, profile.Id);
        Assert.Equal(email, profile.Email);
        Assert.True(profile.EmailConfirmed);
        Assert.Equal([AppRoles.User], profile.Roles);
        Assert.Equal(userPermissions, profile.Permissions);

        // Refresh: 200 with a new pair.
        var refresh = await client.PostAsJsonAsync("/api/v1/Auth/Refresh", new RefreshRequest(tokens.RefreshToken), ct);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var refreshed = await refresh.Content.ReadFromJsonAsync<TokenResponse>(ct);
        Assert.NotNull(refreshed);
        Assert.NotEqual(tokens.RefreshToken, refreshed.RefreshToken);
        Assert.NotEqual(tokens.AccessToken, refreshed.AccessToken);

        // Logout: 204, then the same access token is rejected.
        var logout = await SendWithTokenAsync(HttpMethod.Post, "/api/v1/Auth/Logout", refreshed.AccessToken, ct);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var meAfterLogout = await SendWithTokenAsync(HttpMethod.Get, "/api/v1/Auth/Me", refreshed.AccessToken, ct);
        await AssertUnauthorizedProblemAsync(meAfterLogout);

        // The session's refresh token is revoked too.
        await AssertUnauthorizedProblemAsync(
            await client.PostAsJsonAsync("/api/v1/Auth/Refresh", new RefreshRequest(refreshed.RefreshToken), ct));
    }

    private Task<HttpResponseMessage> SendWithTokenAsync(HttpMethod method, string url, string accessToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(request, ct);
    }

    private static async Task<ProblemDetails> AssertUnauthorizedProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(401, problem.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
        return problem;
    }
}
