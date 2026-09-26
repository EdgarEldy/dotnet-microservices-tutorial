using Customer.API.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Customer.API.Tests.Data;

/// <summary>The migrations and the constraints they create, checked against the real schema.</summary>
public sealed class AppDbContextTests(PostgreSqlFixture postgres) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task MigrateAsync_ShouldApplyEveryMigration_WhenDatabaseIsEmpty()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);

        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        var pending = await dbContext.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken);
        var applied = await dbContext.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);

        Assert.Empty(pending);
        Assert.Equal(dbContext.Database.GetMigrations(), applied);
        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task SaveChangesAsync_ShouldThrowUniqueViolation_WhenUserIdAlreadyHasProfile()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        await using (var arrange = PostgreSqlFixture.CreateDbContext(connectionString))
        {
            arrange.Customers.Add(NewCustomer(userId: 42));
            await arrange.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        dbContext.Customers.Add(NewCustomer(userId: 42));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
    }

    [Fact]
    public async Task SaveChangesAsync_ShouldAcceptAnyUserId_WhenNoUserTableExists()
    {
        // UserId is a plain column: no foreign key, so an id only identity-api knows is stored as is.
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);

        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        dbContext.Customers.Add(NewCustomer(userId: 987_654));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(await dbContext.Customers.AnyAsync(c => c.UserId == 987_654, TestContext.Current.CancellationToken));
    }

    private static Models.Customer NewCustomer(int userId) => new()
    {
        UserId = userId,
        FirstName = "Ada",
        LastName = "Lovelace",
        Telephone = "+44 20 7946 0000",
        Email = "ada@example.com",
        Address = "12 St James's Square, London",
    };
}
