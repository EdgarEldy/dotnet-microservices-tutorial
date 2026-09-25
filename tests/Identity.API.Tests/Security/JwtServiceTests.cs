using System.Security.Claims;
using Identity.API.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Identity.API.Tests.Security;

public sealed class JwtServiceTests
{
    private const string SigningKey = "jwt-service-tests-signing-key-0123456789";

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider timeProvider = new(Now);

    private readonly JwtOptions options = new()
    {
        SigningKey = SigningKey,
        Issuer = "identity-api",
        Audience = "dotnet-microservices-tutorial",
        AccessTokenLifetime = TimeSpan.FromMinutes(15),
        RefreshTokenLifetime = TimeSpan.FromDays(7),
    };

    private readonly IdentityOptions identityOptions = new();

    private JwtService CreateService() =>
        new(Options.Create(options), Options.Create(identityOptions), timeProvider);

    [Fact]
    public async Task CreateAccessToken_ShouldCarrySubjectPermissionsJtiAndSid_WhenClaimsAreGiven()
    {
        var sessionId = Guid.NewGuid();
        Claim[] claims =
        [
            new(AppClaimTypes.UserId, "42"),
            new(AppClaimTypes.Email, "user@example.com"),
            new(AppClaimTypes.Role, "User"),
            new(AppClaimTypes.Permission, "ORDER:READ"),
            new(AppClaimTypes.Permission, "ORDER:WRITE"),
        ];

        var accessToken = CreateService().CreateAccessToken(claims, sessionId);

        var validation = await ValidateAsync(accessToken.Token);
        Assert.True(validation.IsValid, validation.Exception?.Message);

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(accessToken.Token);
        Assert.Equal("42", jwt.Subject);
        Assert.Equal(accessToken.Jti, jwt.Id);
        Assert.Equal(sessionId.ToString(), jwt.GetClaim(AppClaimTypes.SessionId).Value);
        Assert.Equal(["ORDER:READ", "ORDER:WRITE"], jwt.Claims.Where(c => c.Type == AppClaimTypes.Permission).Select(c => c.Value));
        Assert.Equal("User", jwt.GetClaim(AppClaimTypes.Role).Value);
        Assert.Equal(options.Issuer, jwt.Issuer);
        Assert.Equal([options.Audience], jwt.Audiences);
        Assert.Equal(SecurityAlgorithms.HmacSha256, jwt.Alg);
    }

    [Fact]
    public void CreateAccessToken_ShouldExpireAfterConfiguredLifetime_WhenTimeProviderIsFake()
    {
        var accessToken = CreateService().CreateAccessToken([new Claim(AppClaimTypes.UserId, "1")], Guid.NewGuid());

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(accessToken.Token);
        Assert.Equal(Now + options.AccessTokenLifetime, accessToken.ExpiresAt);
        Assert.Equal((Now + options.AccessTokenLifetime).UtcDateTime, jwt.ValidTo);
        Assert.Equal(Now.UtcDateTime, jwt.IssuedAt);
        Assert.Equal(Now.UtcDateTime, jwt.ValidFrom);
    }

    [Fact]
    public void CreateAccessToken_ShouldIssueDistinctJti_WhenCalledTwiceWithSameInput()
    {
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        Claim[] claims = [new(AppClaimTypes.UserId, "1")];

        var first = service.CreateAccessToken(claims, sessionId);
        var second = service.CreateAccessToken(claims, sessionId);

        Assert.NotEqual(first.Jti, second.Jti);
        Assert.NotEqual(
            new JsonWebTokenHandler().ReadJsonWebToken(first.Token).Id,
            new JsonWebTokenHandler().ReadJsonWebToken(second.Token).Id);
    }

    [Fact]
    public void CreateAccessToken_ShouldDropSecurityStampAndReplaceIncomingJtiAndSid_WhenClaimsContainThem()
    {
        var sessionId = Guid.NewGuid();
        Claim[] claims =
        [
            new(AppClaimTypes.UserId, "1"),
            new(identityOptions.ClaimsIdentity.SecurityStampClaimType, "secret-stamp"),
            new(AppClaimTypes.TokenId, "stale-jti"),
            new(AppClaimTypes.SessionId, "stale-sid"),
        ];

        var accessToken = CreateService().CreateAccessToken(claims, sessionId);

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(accessToken.Token);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == identityOptions.ClaimsIdentity.SecurityStampClaimType);
        Assert.DoesNotContain(jwt.Claims, c => c.Value is "secret-stamp" or "stale-jti" or "stale-sid");
        Assert.Single(jwt.Claims, c => c.Type == AppClaimTypes.TokenId);
        Assert.Equal(sessionId.ToString(), Assert.Single(jwt.Claims, c => c.Type == AppClaimTypes.SessionId).Value);
    }

    [Fact]
    public async Task CreateAccessToken_ShouldFailValidation_WhenCheckedWithAnotherKey()
    {
        var accessToken = CreateService().CreateAccessToken([new Claim(AppClaimTypes.UserId, "1")], Guid.NewGuid());

        var validation = await ValidateAsync(accessToken.Token, "another-signing-key-of-sufficient-length-00");

        Assert.False(validation.IsValid);
    }

    [Fact]
    public void CreateRefreshToken_ShouldReturnRandomTokenWithItsSha256Hash_WhenCalled()
    {
        var service = CreateService();

        var first = service.CreateRefreshToken();
        var second = service.CreateRefreshToken();

        Assert.NotEqual(first.Token, second.Token);
        Assert.Equal(service.HashRefreshToken(first.Token), first.Hash);
        Assert.Matches("^[0-9a-f]{64}$", first.Hash);
        Assert.DoesNotContain(first.Token, first.Hash, StringComparison.Ordinal);
        Assert.Equal(Now + options.RefreshTokenLifetime, first.ExpiresAt);
    }

    [Fact]
    public void HashRefreshToken_ShouldReturnLowercaseHexSha256_WhenGivenKnownValue()
    {
        // SHA-256("abc"), FIPS 180-2 test vector.
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            CreateService().HashRefreshToken("abc"));
    }

    private Task<TokenValidationResult> ValidateAsync(string token, string key = SigningKey) =>
        new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = options.Issuer,
            ValidAudience = options.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            // The token is dated by the FakeTimeProvider, not by the wall clock: lifetime is asserted separately.
            ValidateLifetime = false,
        });
}
