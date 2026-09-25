using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ServiceDefaults.Tests;

/// <summary>
/// AddDefaultAuthentication as every business service uses it: local validation of identity-api's
/// HS256 tokens and RESOURCE:ACTION policies resolved on the fly by PermissionPolicyProvider.
/// </summary>
public sealed class AuthenticationExtensionsTests
{
    // 512 bits, so the same key can also sign the HS512 token the service must reject.
    private const string SigningKey = "service-defaults-tests-signing-key-long-enough-for-hs256-and-hs512";
    private const string Issuer = "identity-api";
    private const string Audience = "dotnet-microservices-tutorial";
    private const string AuthenticatedPath = "/authenticated";
    private const string PermissionPath = "/permission";
    private const string ClaimsPath = "/claims";
    private const string TestPermission = "ORDERS:CANCEL";

    [Fact]
    public async Task AddDefaultAuthentication_ShouldAuthenticate_WhenTokenIsValid()
    {
        await using var app = await StartAppAsync();

        using var response = await SendAsync(app, AuthenticatedPath, CreateToken());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AddDefaultAuthentication_ShouldReturnUnauthorized_WhenNoTokenIsSent()
    {
        await using var app = await StartAppAsync();

        using var response = await SendAsync(app, AuthenticatedPath, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AddDefaultAuthentication_ShouldReturnUnauthorized_WhenTokenIsExpired()
    {
        await using var app = await StartAppAsync();
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;

        // Expired well beyond the 30 second clock skew.
        using var response = await SendAsync(
            app, AuthenticatedPath, CreateToken(issuedAt: now.AddHours(-2), expires: now.AddHours(-1)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_token", response.Headers.WwwAuthenticate.ToString());
        Assert.Contains("expired", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task AddDefaultAuthentication_ShouldReturnUnauthorized_WhenTokenIsSignedWithAnotherKey()
    {
        await using var app = await StartAppAsync();

        using var response = await SendAsync(
            app, AuthenticatedPath, CreateToken(signingKey: "another-signing-key-also-at-least-256-bits-long"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_token", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task AddDefaultAuthentication_ShouldReturnUnauthorized_WhenTokenHasAnotherIssuer()
    {
        await using var app = await StartAppAsync();

        using var response = await SendAsync(app, AuthenticatedPath, CreateToken(issuer: "someone-else"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AddDefaultAuthentication_ShouldReturnUnauthorized_WhenTokenHasAnotherAudience()
    {
        await using var app = await StartAppAsync();

        using var response = await SendAsync(app, AuthenticatedPath, CreateToken(audience: "another-audience"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AddDefaultAuthentication_ShouldReturnUnauthorized_WhenTokenIsNotSignedWithHs256()
    {
        await using var app = await StartAppAsync();

        // Same key, but HS512: only identity-api's algorithm is accepted.
        using var response = await SendAsync(
            app, AuthenticatedPath, CreateToken(algorithm: SecurityAlgorithms.HmacSha512));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AddDefaultAuthentication_ShouldKeepShortClaimNames_WhenTokenIsValid()
    {
        await using var app = await StartAppAsync();

        using var response = await SendAsync(
            app, ClaimsPath, CreateToken(subject: "user-42", role: "Admin", permissions: [TestPermission]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal($"sub=user-42;name=test-user;isAdmin=True;permission={TestPermission}", body);
    }

    [Fact]
    public async Task AddDefaultAuthentication_ShouldAllowRequest_WhenTokenHoldsThePolicyPermission()
    {
        await using var app = await StartAppAsync();

        using var response = await SendAsync(
            app, PermissionPath, CreateToken(permissions: ["CATALOG:WRITE", TestPermission]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData]
    [InlineData("ORDERS:READ")]
    [InlineData("orders:cancel")]
    [InlineData("ORDERS:CANCEL:ALL")]
    public async Task AddDefaultAuthentication_ShouldReturnForbidden_WhenTokenLacksThePolicyPermission(params string[] permissions)
    {
        await using var app = await StartAppAsync();

        using var response = await SendAsync(app, PermissionPath, CreateToken(permissions: permissions));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AddDefaultAuthentication_ShouldReturnForbidden_WhenOnlyTheRoleMatchesThePolicyName()
    {
        await using var app = await StartAppAsync();

        using var response = await SendAsync(app, PermissionPath, CreateToken(role: TestPermission));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AddDefaultAuthentication_ShouldReturnUnauthorized_WhenPermissionPolicyIsCalledWithoutToken()
    {
        await using var app = await StartAppAsync();

        using var response = await SendAsync(app, PermissionPath, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AddDefaultAuthentication_ShouldFailToStart_WhenSigningKeyIsShorterThan256Bits()
    {
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => StartAppAsync(signingKey: "too-short"));

        Assert.Contains(nameof(JwtValidationOptions.SigningKey), exception.Message);
    }

    [Theory]
    [InlineData("ORDERS:CANCEL")]
    [InlineData("A:B")]
    public async Task GetPolicyAsync_ShouldRequireThatPermissionClaim_WhenPolicyNameIsResourceAction(string policyName)
    {
        var provider = new PermissionPolicyProvider(Options.Create(new AuthorizationOptions()));

        var policy = await provider.GetPolicyAsync(policyName);

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
        var claim = Assert.Single(policy.Requirements.OfType<Microsoft.AspNetCore.Authorization.Infrastructure.ClaimsAuthorizationRequirement>());
        Assert.Equal(AuthenticationExtensions.PermissionClaimType, claim.ClaimType);
        Assert.Equal([policyName], claim.AllowedValues);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData(":WRITE")]
    [InlineData("CATALOG:")]
    [InlineData("")]
    public async Task GetPolicyAsync_ShouldReturnNull_WhenPolicyNameIsNotResourceAction(string policyName)
    {
        var provider = new PermissionPolicyProvider(Options.Create(new AuthorizationOptions()));

        var policy = await provider.GetPolicyAsync(policyName);

        Assert.Null(policy);
    }

    [Fact]
    public async Task GetPolicyAsync_ShouldReturnRegisteredPolicy_WhenPolicyWithThatNameIsDeclared()
    {
        var registered = new AuthorizationPolicyBuilder().RequireRole("Admin").Build();
        var options = new AuthorizationOptions();
        options.AddPolicy(TestPermission, registered);
        var provider = new PermissionPolicyProvider(Options.Create(options));

        var policy = await provider.GetPolicyAsync(TestPermission);

        Assert.Same(registered, policy);
    }

    private static async Task<WebApplication> StartAppAsync(string signingKey = SigningKey)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = signingKey,
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
        });

        builder.AddDefaultAuthentication();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet(AuthenticatedPath, () => "ok").RequireAuthorization();
        app.MapGet(PermissionPath, () => "ok").RequireAuthorization(TestPermission);
        app.MapGet(ClaimsPath, (ClaimsPrincipal user) =>
            $"sub={user.FindFirstValue("sub")};name={user.Identity?.Name};isAdmin={user.IsInRole("Admin")};" +
            $"permission={user.FindFirstValue(AuthenticationExtensions.PermissionClaimType)}")
            .RequireAuthorization();

        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        return app;
    }

    private static async Task<HttpResponseMessage> SendAsync(WebApplication app, string path, string? token)
    {
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string CreateToken(
        string subject = "user-1",
        string role = "Customer",
        string[]? permissions = null,
        string issuer = Issuer,
        string audience = Audience,
        string signingKey = SigningKey,
        string algorithm = SecurityAlgorithms.HmacSha256,
        DateTime? issuedAt = null,
        DateTime? expires = null)
    {
        var now = issuedAt ?? TimeProvider.System.GetUtcNow().UtcDateTime;

        var identity = new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, subject),
            new Claim(AuthenticationExtensions.NameClaimType, "test-user"),
            new Claim(AuthenticationExtensions.RoleClaimType, role),
        ]);
        identity.AddClaims((permissions ?? []).Select(p => new Claim(AuthenticationExtensions.PermissionClaimType, p)));

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = identity,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires ?? now.AddMinutes(15),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), algorithm),
        });
    }
}
