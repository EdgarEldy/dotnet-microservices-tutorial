using System.Globalization;
using Identity.API.Dtos;
using Identity.API.Models;
using Identity.API.Security;
using Identity.API.Services;
using Identity.API.Tests.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Identity.API.Tests.Services;

/// <summary>
/// AuthService's refresh token rotation, reuse detection and logout, persisted in a real
/// PostgreSQL (ExecuteUpdate, the conditional revocation and the transactions cannot be
/// meaningfully mocked). The service is resolved from identity-api's own container.
/// </summary>
public sealed class AuthServiceIntegrationTests(IdentityApiFixture fixture) : IClassFixture<IdentityApiFixture>
{
    [Fact]
    public async Task RefreshAsync_ShouldKeepFamilyAndChainReplacedByTokenId_WhenRefreshTokenIsValid()
    {
        var (user, login) = await LoginNewUserAsync("rotation");

        var refreshed = await RunAsync(auth => auth.RefreshAsync(new RefreshRequest(login.RefreshToken), CancellationToken.None));

        var tokens = await GetTokensAsync(user.Id);
        Assert.Equal(2, tokens.Count);
        var original = tokens.Single(t => t.TokenHash == Sha256Hex(login.RefreshToken));
        var rotated = tokens.Single(t => t.TokenHash == Sha256Hex(refreshed.RefreshToken));

        Assert.Equal(original.FamilyId, rotated.FamilyId);
        Assert.Equal(rotated.Id, original.ReplacedByTokenId);
        Assert.NotNull(original.RevokedAt);
        Assert.Null(rotated.RevokedAt);
        Assert.Null(rotated.ReplacedByTokenId);

        // The access tokens of one session share its "sid": the family id.
        Assert.Equal(original.FamilyId.ToString(), ReadClaim(refreshed.AccessToken, AppClaimTypes.SessionId));
        Assert.Equal(original.FamilyId.ToString(), ReadClaim(login.AccessToken, AppClaimTypes.SessionId));
    }

    [Fact]
    public async Task RefreshAsync_ShouldRevokeWholeFamily_WhenRotatedRefreshTokenIsReused()
    {
        var (user, login) = await LoginNewUserAsync("reuse");
        var refreshed = await RunAsync(auth => auth.RefreshAsync(new RefreshRequest(login.RefreshToken), CancellationToken.None));

        await Assert.ThrowsAsync<AuthenticationFailedException>(() =>
            RunAsync(auth => auth.RefreshAsync(new RefreshRequest(login.RefreshToken), CancellationToken.None)));

        var tokens = await GetTokensAsync(user.Id);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
        Assert.True(await fixture.QueryAsync(db => db.AuditLogs.AnyAsync(a =>
            a.ActorUserId == user.Id && a.Action == AuditActions.RefreshTokenReuseDetected)));

        // The legitimate holder's latest token is dead too: the whole session is closed.
        await Assert.ThrowsAsync<AuthenticationFailedException>(() =>
            RunAsync(auth => auth.RefreshAsync(new RefreshRequest(refreshed.RefreshToken), CancellationToken.None)));
    }

    [Fact]
    public async Task RefreshAsync_ShouldRevokeFamily_WhenSecurityStampChangedSinceIssuance()
    {
        var (user, login) = await LoginNewUserAsync("stamp");
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var tracked = await userManager.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture));
            await userManager.UpdateSecurityStampAsync(tracked!);
        }

        await Assert.ThrowsAsync<AuthenticationFailedException>(() =>
            RunAsync(auth => auth.RefreshAsync(new RefreshRequest(login.RefreshToken), CancellationToken.None)));

        var tokens = await GetTokensAsync(user.Id);
        var token = Assert.Single(tokens);
        Assert.NotNull(token.RevokedAt);
        Assert.Null(token.ReplacedByTokenId);
    }

    [Fact]
    public async Task RefreshAsync_ShouldThrowAuthenticationFailed_WhenRefreshTokenIsUnknown()
    {
        await Assert.ThrowsAsync<AuthenticationFailedException>(() =>
            RunAsync(auth => auth.RefreshAsync(new RefreshRequest("never-issued-refresh-token"), CancellationToken.None)));
    }

    [Fact]
    public async Task LogoutAsync_ShouldBlacklistJtiAndRevokeOnlyThatSession_WhenSessionIsActive()
    {
        var (user, login) = await LoginNewUserAsync("logout");
        var otherDevice = await RunAsync(auth => auth.LoginAsync(new LoginRequest(user.Email!, IdentityApiFixture.ValidPassword), CancellationToken.None));
        var session = ReadSession(login.AccessToken);

        await RunAsync(async auth =>
        {
            await auth.LogoutAsync(session, CancellationToken.None);
            return true;
        });

        var blacklisted = await fixture.QueryAsync(db => db.BlacklistedAccessTokens.SingleAsync(t => t.Jti == session.Jti));
        Assert.Equal(user.Id, blacklisted.UserId);
        Assert.Equal(session.AccessTokenExpiresAt, blacklisted.ExpiresAt);
        Assert.True(await RunAsync(auth => auth.IsAccessTokenRevokedAsync(session.Jti, CancellationToken.None)));

        var tokens = await GetTokensAsync(user.Id);
        Assert.NotNull(tokens.Single(t => t.FamilyId == session.SessionId).RevokedAt);
        Assert.Null(tokens.Single(t => t.TokenHash == Sha256Hex(otherDevice.RefreshToken)).RevokedAt);
        Assert.False(await RunAsync(auth => auth.IsAccessTokenRevokedAsync(ReadSession(otherDevice.AccessToken).Jti, CancellationToken.None)));
    }

    private async Task<(AppUser User, TokenResponse Login)> LoginNewUserAsync(string prefix)
    {
        var email = IdentityApiFixture.NewEmail(prefix);
        var user = await fixture.CreateConfirmedUserAsync(email);
        var login = await RunAsync(auth => auth.LoginAsync(new LoginRequest(email, IdentityApiFixture.ValidPassword), CancellationToken.None));
        return (user, login);
    }

    /// <summary>One call on a fresh request-like scope, as a controller would make it.</summary>
    private async Task<T> RunAsync<T>(Func<IAuthService, Task<T>> call)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        return await call(scope.ServiceProvider.GetRequiredService<IAuthService>());
    }

    private Task<List<RefreshToken>> GetTokensAsync(int userId) =>
        fixture.QueryAsync(db => db.RefreshTokens.Where(t => t.UserId == userId).ToListAsync());

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private static string? ReadClaim(string accessToken, string type) =>
        new JsonWebTokenHandler().ReadJsonWebToken(accessToken).Claims.FirstOrDefault(c => c.Type == type)?.Value;

    private static CurrentSession ReadSession(string accessToken)
    {
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(accessToken);
        return new CurrentSession(
            int.Parse(jwt.Subject, CultureInfo.InvariantCulture),
            jwt.Id,
            Guid.Parse(ReadClaim(accessToken, AppClaimTypes.SessionId)!),
            new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero));
    }
}
