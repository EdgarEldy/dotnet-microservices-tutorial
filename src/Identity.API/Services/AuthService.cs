using System.Globalization;
using Identity.API.Data;
using Identity.API.Dtos;
using Identity.API.Models;
using Identity.API.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Identity.API.Services;

public sealed class AuthService(
    AppDbContext dbContext,
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager,
    IUserClaimsPrincipalFactory<AppUser> claimsPrincipalFactory,
    IJwtService jwtService,
    TimeProvider timeProvider,
    ILogger<AuthService> logger) : IAuthService
{
    // Verified against when the e-mail is unknown, so that case costs the same hashing time as a
    // wrong password and the response time does not reveal whether the account exists.
    private static readonly Lazy<string> DummyPasswordHash =
        new(() => new PasswordHasher<AppUser>().HashPassword(new AppUser(), Guid.NewGuid().ToString()));

    public async Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            userManager.PasswordHasher.VerifyHashedPassword(new AppUser(), DummyPasswordHash.Value, request.Password);
            logger.LogWarning("Login failed: unknown account");
            throw new AuthenticationFailedException();
        }

        // Checks, in order: confirmed e-mail (SignIn.RequireConfirmedEmail), lockout, password.
        // A wrong password counts toward the lockout.
        var result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            if (result.IsLockedOut || result.IsNotAllowed)
            {
                // Identity refuses these before hashing the password: hash anyway, so an unconfirmed or
                // locked account answers as slowly as a wrong password and its state stays hidden.
                userManager.PasswordHasher.VerifyHashedPassword(user, DummyPasswordHash.Value, request.Password);
            }

            var reason = result.IsLockedOut ? "LockedOut" : result.IsNotAllowed ? "NotAllowed" : "InvalidPassword";
            AddAuditLog(user.Id, AuditActions.LoginFailed, AuditActions.UserEntity, user.Id, reason);
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogWarning("Login failed for user {UserId}: {Reason}", user.Id, reason);
            throw new AuthenticationFailedException();
        }

        // A login opens a new session: a new refresh token family, also the "sid" of its access tokens.
        var (response, _) = await IssueTokenPairAsync(user, familyId: Guid.CreateVersion7(timeProvider.GetUtcNow()));

        AddAuditLog(user.Id, AuditActions.LoginSucceeded, AuditActions.UserEntity, user.Id);
        await dbContext.SaveChangesAsync(cancellationToken);

        return response;
    }

    public async Task<TokenResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        var tokenHash = jwtService.HashRefreshToken(request.RefreshToken);
        var current = await dbContext.RefreshTokens
            .Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        if (current is null)
        {
            logger.LogWarning("Refresh failed: unknown refresh token");
            throw new AuthenticationFailedException();
        }

        var now = timeProvider.GetUtcNow();

        if (current.RevokedAt is not null)
        {
            if (current.ReplacedByTokenId is not null)
            {
                // An already rotated token presented again: one of its two holders stole it.
                // Revoke the whole family, the legitimate user simply logs in again.
                await RevokeFamilyAsync(current, AuditActions.RefreshTokenReuseDetected, cancellationToken);
            }
            else
            {
                // Revoked by a logout, a password reset or an earlier family revocation.
                logger.LogWarning("Refresh failed for user {UserId}: refresh token revoked", current.UserId);
            }

            throw new AuthenticationFailedException();
        }

        if (current.ExpiresAt <= now)
        {
            logger.LogWarning("Refresh failed for user {UserId}: refresh token expired", current.UserId);
            throw new AuthenticationFailedException();
        }

        var user = current.User;
        var securityStamp = await userManager.GetSecurityStampAsync(user);
        if (securityStamp != current.SecurityStampAtIssuance
            || !await signInManager.CanSignInAsync(user)
            || await userManager.IsLockedOutAsync(user))
        {
            // Password reset, security stamp renewal, lockout...: the session must not survive it.
            await RevokeFamilyAsync(current, AuditActions.RefreshTokenFamilyRevoked, cancellationToken);
            throw new AuthenticationFailedException();
        }

        var (response, next) = await IssueTokenPairAsync(user, current.FamilyId);

        // Insert the new token and revoke the current one in one transaction. The revocation is a
        // conditional UPDATE: when two requests race with the same token, only one of them sees
        // it still active; the other one is treated as a reuse.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        var rotated = await strategy.ExecuteAsync(async ct =>
        {
            // On a retry, the previous attempt may have inserted the new token before rolling back.
            dbContext.Entry(next).State = EntityState.Added;

            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
            await dbContext.SaveChangesAsync(ct);

            var revoked = await dbContext.RefreshTokens
                .Where(t => t.Id == current.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.RevokedAt, now)
                    .SetProperty(t => t.ReplacedByTokenId, next.Id), ct);

            if (revoked == 0)
            {
                await transaction.RollbackAsync(ct);
                return false;
            }

            await transaction.CommitAsync(ct);
            return true;
        }, cancellationToken);

        if (!rotated)
        {
            dbContext.Entry(next).State = EntityState.Detached;
            await RevokeFamilyAsync(current, AuditActions.RefreshTokenReuseDetected, cancellationToken);
            throw new AuthenticationFailedException();
        }

        return response;
    }

    public async Task LogoutAsync(CurrentSession session, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        if (!await dbContext.BlacklistedAccessTokens.AnyAsync(t => t.Jti == session.Jti, cancellationToken))
        {
            dbContext.BlacklistedAccessTokens.Add(new BlacklistedAccessToken
            {
                UserId = session.UserId,
                Jti = session.Jti,
                BlacklistedAt = now,
                ExpiresAt = session.AccessTokenExpiresAt,
            });
        }

        // Only this session ends: the user's other devices keep their own families.
        await dbContext.RefreshTokens
            .Where(t => t.FamilyId == session.SessionId && t.UserId == session.UserId && t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, now), cancellationToken);

        AddAuditLog(session.UserId, AuditActions.LoggedOut, AuditActions.UserEntity, session.UserId);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("User {UserId} logged out of session {SessionId}", session.UserId, session.SessionId);
    }

    public Task<bool> IsAccessTokenRevokedAsync(string jti, CancellationToken cancellationToken) =>
        dbContext.BlacklistedAccessTokens.AsNoTracking().AnyAsync(t => t.Jti == jti, cancellationToken);

    /// <summary>
    /// Signs an access token and creates the matching refresh token entity (tracked, not saved:
    /// the caller saves it with the rest of its unit of work). Only the refresh token's hash is stored.
    /// </summary>
    private async Task<(TokenResponse Response, RefreshToken Entity)> IssueTokenPairAsync(AppUser user, Guid familyId)
    {
        var principal = await claimsPrincipalFactory.CreateAsync(user);
        var accessToken = jwtService.CreateAccessToken(principal.Claims, familyId);
        var refreshToken = jwtService.CreateRefreshToken();
        var now = timeProvider.GetUtcNow();

        var entity = new RefreshToken
        {
            Id = Guid.CreateVersion7(now),
            UserId = user.Id,
            TokenHash = refreshToken.Hash,
            FamilyId = familyId,
            SecurityStampAtIssuance = await userManager.GetSecurityStampAsync(user),
            CreatedAt = now,
            ExpiresAt = refreshToken.ExpiresAt,
        };
        dbContext.RefreshTokens.Add(entity);

        var response = new TokenResponse(
            accessToken.Token, accessToken.ExpiresAt, refreshToken.Token, refreshToken.ExpiresAt);

        return (response, entity);
    }

    private async Task RevokeFamilyAsync(RefreshToken token, string auditAction, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await dbContext.RefreshTokens
            .Where(t => t.FamilyId == token.FamilyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, now), cancellationToken);

        AddAuditLog(token.UserId, auditAction, AuditActions.RefreshTokenEntity, token.Id,
            $"Family {token.FamilyId} revoked");
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogWarning("Refresh token family {FamilyId} of user {UserId} revoked: {Reason}",
            token.FamilyId, token.UserId, auditAction);
    }

    private void AddAuditLog(int? actorUserId, string action, string entityType, object entityId, string? details = null) =>
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actorUserId,
            Action = action,
            EntityType = entityType,
            EntityId = Convert.ToString(entityId, CultureInfo.InvariantCulture),
            Details = details,
            CreatedAt = timeProvider.GetUtcNow(),
        });
}
