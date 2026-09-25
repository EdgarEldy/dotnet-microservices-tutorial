using Identity.API.Data;
using Identity.API.Models;
using Identity.API.Security;
using Identity.API.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Identity.API.Tests.TestSupport;

namespace Identity.API.Tests.Data;

/// <summary>
/// identity-db's schema, seeding and constraints against a real PostgreSQL: the EF migrations
/// and <see cref="DatabaseInitializer"/> exactly as identity-api runs them at startup.
/// </summary>
public sealed class AppDbContextTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task MigrateAsync_ShouldApplyEveryMigrationAndMatchTheModel_WhenDatabaseIsEmpty()
    {
        await RunInitializerAsync();

        await using var scope = fixture.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(dbContext.Database.GetMigrations(), await dbContext.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.False(dbContext.Database.HasPendingModelChanges());

        // The outbox tables the README's transactional outbox relies on exist in identity-db.
        Assert.Equal(0, await dbContext.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>().CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await dbContext.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxState>().CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartAsync_ShouldSeedRolesAndPermissionsOnce_WhenRunTwice()
    {
        await RunInitializerAsync();
        var first = await CountSeedAsync();

        await RunInitializerAsync();
        var second = await CountSeedAsync();

        var expectedGrants = AppRoles.PermissionsByRole.Values.Sum(p => p.Count);
        Assert.Equal((AppPermissions.All.Count, AppRoles.PermissionsByRole.Count, expectedGrants), first);
        Assert.Equal(first, second);

        await using var scope = fixture.Services.CreateAsyncScope();
        var roleService = scope.ServiceProvider.GetRequiredService<IRoleService>();
        Assert.Equal(
            AppRoles.PermissionsByRole[AppRoles.User].Order(StringComparer.Ordinal),
            await roleService.GetPermissionsAsync([AppRoles.User], TestContext.Current.CancellationToken));
        Assert.Equal(
            AppPermissions.All.Order(StringComparer.Ordinal),
            await roleService.GetPermissionsAsync([AppRoles.User, AppRoles.Admin], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveChangesAsync_ShouldThrowDbUpdateException_WhenRefreshTokenHashIsDuplicated()
    {
        await RunInitializerAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var email = $"hash-{Guid.NewGuid():N}@example.com";
        var user = new AppUser { UserName = email, Email = email };
        Assert.True((await userManager.CreateAsync(user)).Succeeded);

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hash = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        dbContext.RefreshTokens.Add(NewRefreshToken(user.Id, hash));
        dbContext.RefreshTokens.Add(NewRefreshToken(user.Id, hash));

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    private async Task RunInitializerAsync()
    {
        var initializer = new DatabaseInitializer(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<DatabaseInitializer>.Instance);
        await initializer.StartAsync(TestContext.Current.CancellationToken);
    }

    private async Task<(int Permissions, int Roles, int Grants)> CountSeedAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ct = TestContext.Current.CancellationToken;
        return (
            await dbContext.Permissions.CountAsync(ct),
            await dbContext.Roles.CountAsync(ct),
            await dbContext.RolePermissions.CountAsync(ct));
    }

    private static RefreshToken NewRefreshToken(int userId, string hash) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TokenHash = hash,
        FamilyId = Guid.NewGuid(),
        SecurityStampAtIssuance = "stamp",
        CreatedAt = DateTimeOffset.UnixEpoch,
        ExpiresAt = DateTimeOffset.UnixEpoch.AddDays(7),
    };
}
