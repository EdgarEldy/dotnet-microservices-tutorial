using System.Globalization;
using Common.Lib.Exceptions;
using Contracts;
using FluentValidation;
using FluentValidation.Results;
using Identity.API.Data;
using Identity.API.Dtos;
using Identity.API.Models;
using Identity.API.Security;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Mapster;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Identity.API.Services;

public sealed class UserService(
    AppDbContext dbContext,
    UserManager<AppUser> userManager,
    IRoleService roleService,
    IPublishEndpoint publishEndpoint,
    TimeProvider timeProvider,
    ILogger<UserService> logger) : IUserService
{
    private const string InvalidTokenMessage = "The token is invalid or has expired.";

    public async Task RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim();

        // Password policy first, for new and existing e-mails alike: otherwise a weak password
        // would get a 400 for a new e-mail but a 202 for a registered one.
        await ValidatePasswordAsync(new AppUser { UserName = email, Email = email }, request.Password);

        if (await userManager.FindByEmailAsync(email) is not null)
        {
            // Same response as a real registration: Register must not reveal which e-mails exist.
            // Hashing anyway keeps the response time close to the real path.
            userManager.PasswordHasher.HashPassword(new AppUser(), request.Password);
            logger.LogInformation("Registration ignored: the e-mail is already registered");
            return;
        }

        // UserManager saves on every call. An explicit transaction makes the user, its role, the
        // audit row and MassTransit's OutboxMessage one atomic unit. Npgsql's retrying execution
        // strategy (enabled by Aspire) requires a user transaction to run inside it.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async ct =>
        {
            ForgetPreviousAttempt();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

            var user = new AppUser { UserName = email, Email = email };
            IdentityResult created;
            try
            {
                created = await userManager.CreateAsync(user, request.Password);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
            })
            {
                // A concurrent registration of the same e-mail committed between Identity's duplicate
                // check and this insert: the unique index rejects it. Same silent outcome as a known
                // e-mail (the transaction is rolled back on dispose), never a 500 that would reveal it.
                logger.LogInformation("Registration ignored: the e-mail was registered concurrently");
                return;
            }

            if (!created.Succeeded)
            {
                if (created.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail)
                    or nameof(IdentityErrorDescriber.DuplicateUserName)))
                {
                    // A concurrent registration of the same e-mail won the race: same silent outcome.
                    logger.LogInformation("Registration ignored: the e-mail is already registered");
                    return;
                }

                throw ToValidationException(created, nameof(RegisterRequest.Password));
            }

            ThrowIfFailed(await userManager.AddToRoleAsync(user, AppRoles.User));

            var confirmationToken = TokenEncoding.Encode(await userManager.GenerateEmailConfirmationTokenAsync(user));

            // Publish BEFORE SaveChangesAsync: the EF outbox stores the message in OutboxMessage
            // through this DbContext, so it commits (or rolls back) together with the user.
            await publishEndpoint.Publish(new UserRegisteredEvent(user.Id, email, confirmationToken), ct);

            AddAuditLog(user.Id, AuditActions.UserRegistered);

            await dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            logger.LogInformation("User {UserId} registered", user.Id);
        }, cancellationToken);
    }

    public async Task ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(request.UserId.ToString(CultureInfo.InvariantCulture));

        // An unknown user id gets the same answer as a bad token: no account enumeration.
        if (user is null || !TokenEncoding.TryDecode(request.Token, out var token))
        {
            throw InvalidToken(nameof(ConfirmEmailRequest.Token));
        }

        var result = await userManager.ConfirmEmailAsync(user, token);
        if (!result.Succeeded)
        {
            throw InvalidToken(nameof(ConfirmEmailRequest.Token));
        }

        AddAuditLog(user.Id, AuditActions.EmailConfirmed);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            logger.LogInformation("Password reset ignored: no account for the e-mail supplied");
            return;
        }

        // Identity's reset token is computed, not stored: the audit row is the only business
        // write. Publish BEFORE SaveChangesAsync, so the audit row and the OutboxMessage are
        // saved by the same call, hence the same transaction.
        var resetToken = TokenEncoding.Encode(await userManager.GeneratePasswordResetTokenAsync(user));
        await publishEndpoint.Publish(new PasswordResetRequestedEvent(user.Id, user.Email!, resetToken), cancellationToken);

        AddAuditLog(user.Id, AuditActions.PasswordResetRequested);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Password reset requested for user {UserId}", user.Id);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(request.Email.Trim());

        // An unknown e-mail gets the same answer as a bad token: no account enumeration.
        if (user is null || !TokenEncoding.TryDecode(request.Token, out var token))
        {
            throw InvalidToken(nameof(ResetPasswordRequest.Token));
        }

        var result = await userManager.ResetPasswordAsync(user, token, request.NewPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken)))
            {
                throw InvalidToken(nameof(ResetPasswordRequest.Token));
            }

            throw ToValidationException(result, nameof(ResetPasswordRequest.NewPassword));
        }

        // ResetPasswordAsync already renewed the security stamp, which invalidates every refresh
        // token at its next use. Revoking them now also closes the sessions right away.
        var now = timeProvider.GetUtcNow();
        await dbContext.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, now), cancellationToken);

        AddAuditLog(user.Id, AuditActions.PasswordReset);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Password reset for user {UserId}", user.Id);
    }

    public async Task<UserProfileResponse> GetProfileAsync(int userId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture))
            ?? throw new ResourceNotFoundException("User", userId);

        var roles = (await userManager.GetRolesAsync(user)).Order(StringComparer.Ordinal).ToList();
        var permissions = await roleService.GetPermissionsAsync(roles, cancellationToken);

        return user.Adapt<UserProfileResponse>() with { Roles = roles, Permissions = permissions };
    }

    private void AddAuditLog(int userId, string action) =>
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = userId,
            Action = action,
            EntityType = AuditActions.UserEntity,
            EntityId = userId.ToString(CultureInfo.InvariantCulture),
            CreatedAt = timeProvider.GetUtcNow(),
        });

    /// <summary>
    /// The execution strategy re-runs the whole operation after a transient failure: whatever a
    /// failed attempt left in the change tracker (the user, its role, the audit row, the previous
    /// OutboxMessage) is forgotten so it is not inserted twice. MassTransit's OutboxState entry is
    /// kept: the scoped outbox context tracks it and either reuses it (never saved) or replaces
    /// it (saved, then rolled back), so the retried Publish always gets a deliverable outbox.
    /// </summary>
    private void ForgetPreviousAttempt()
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is not OutboxState)
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private async Task ValidatePasswordAsync(AppUser user, string password)
    {
        var errors = new List<IdentityError>();
        foreach (var validator in userManager.PasswordValidators)
        {
            var result = await validator.ValidateAsync(userManager, user, password);
            errors.AddRange(result.Errors);
        }

        if (errors.Count > 0)
        {
            throw ToValidationException(IdentityResult.Failed([.. errors]), nameof(RegisterRequest.Password));
        }
    }

    private static ValidationException InvalidToken(string propertyName) =>
        new([new ValidationFailure(propertyName, InvalidTokenMessage)]);

    private static ValidationException ToValidationException(IdentityResult result, string passwordPropertyName) =>
        new(result.Errors.Select(error => new ValidationFailure(
            error.Code.StartsWith("Password", StringComparison.Ordinal) ? passwordPropertyName : string.Empty,
            error.Description)));

    private static void ThrowIfFailed(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(e => e.Description)));
        }
    }
}
