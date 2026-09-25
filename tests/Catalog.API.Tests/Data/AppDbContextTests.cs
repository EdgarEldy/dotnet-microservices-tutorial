using Catalog.API.Models;
using Catalog.API.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Catalog.API.Tests.Data;

/// <summary>The constraints the migrations create, checked against the real schema.</summary>
public sealed class AppDbContextTests(PostgreSqlFixture postgres) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task SaveChangesAsync_ShouldThrowUniqueViolation_WhenCategoryNameAlreadyExists()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        await using (var arrange = PostgreSqlFixture.CreateDbContext(connectionString))
        {
            arrange.Categories.Add(new Category { CategoryName = "Books" });
            await arrange.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        dbContext.Categories.Add(new Category { CategoryName = "Books" });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
    }

    [Fact]
    public async Task SaveChangesAsync_ShouldThrowForeignKeyViolation_WhenProductReferencesUnknownCategory()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);

        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        dbContext.Products.Add(new Product { CategoryId = 999_999, ProductName = "Orphan", UnitPrice = 1m });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgresException.SqlState);
    }

    [Fact]
    public async Task SaveChangesAsync_ShouldRoundUnitPriceToTwoDecimals_WhenPriceHasMoreDecimals()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        int productId;
        await using (var arrange = PostgreSqlFixture.CreateDbContext(connectionString))
        {
            var category = new Category { CategoryName = "Books" };
            var product = new Product { Category = category, ProductName = "Precise", UnitPrice = 10.456m };
            arrange.Products.Add(product);
            await arrange.SaveChangesAsync(TestContext.Current.CancellationToken);
            productId = product.Id;
        }

        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        var stored = await dbContext.Products.SingleAsync(p => p.Id == productId, TestContext.Current.CancellationToken);

        // numeric(18,2): the column itself keeps two decimals.
        Assert.Equal(10.46m, stored.UnitPrice);
    }
}
