using Catalog.API.Data;
using Catalog.API.Models;
using Catalog.API.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Catalog.API.Tests.Data;

/// <summary>
/// The startup step against a real, initially empty PostgreSQL database: the real migrations,
/// then the sample seed, run as many times as the service restarts.
/// </summary>
public sealed class DatabaseInitializerTests(PostgreSqlFixture postgres) : IClassFixture<PostgreSqlFixture>
{
    private const int SampleCategoryCount = 3;
    private const int SampleProductCount = 6;

    [Fact]
    public async Task StartAsync_ShouldApplyEveryMigration_WhenDatabaseIsEmpty()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync(TestContext.Current.CancellationToken);

        await RunInitializerAsync(connectionString, seedSampleData: false);

        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        var pending = await dbContext.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken);
        var applied = await dbContext.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        Assert.Empty(pending);
        Assert.Equal(dbContext.Database.GetMigrations(), applied);
        Assert.Equal(0, await dbContext.Categories.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await dbContext.Products.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartAsync_ShouldSeedSampleCatalog_WhenSeedSampleDataIsEnabled()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync(TestContext.Current.CancellationToken);

        await RunInitializerAsync(connectionString, seedSampleData: true);

        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        var categories = await dbContext.Categories
            .Include(c => c.Products)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SampleCategoryCount, categories.Count);
        Assert.Equal(SampleProductCount, categories.Sum(c => c.Products.Count));
        Assert.All(categories, c => Assert.NotEmpty(c.Products));
        Assert.All(categories.SelectMany(c => c.Products), p => Assert.True(p.UnitPrice > 0));
    }

    [Fact]
    public async Task StartAsync_ShouldNotDuplicateSampleData_WhenRunTwice()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync(TestContext.Current.CancellationToken);

        await RunInitializerAsync(connectionString, seedSampleData: true);
        await RunInitializerAsync(connectionString, seedSampleData: true);

        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        Assert.Equal(SampleCategoryCount, await dbContext.Categories.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SampleProductCount, await dbContext.Products.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartAsync_ShouldInsertOnlyMissingSampleProducts_WhenCatalogIsPartiallySeeded()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        int booksId;
        await using (var arrange = PostgreSqlFixture.CreateDbContext(connectionString))
        {
            var books = new Category { CategoryName = "Books" };
            books.Products.Add(new Product { ProductName = "Domain-Driven Design", UnitPrice = 54.99m });
            arrange.Categories.Add(books);
            await arrange.SaveChangesAsync(TestContext.Current.CancellationToken);
            booksId = books.Id;
        }

        await RunInitializerAsync(connectionString, seedSampleData: true);

        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        Assert.Equal(SampleCategoryCount, await dbContext.Categories.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SampleProductCount, await dbContext.Products.CountAsync(TestContext.Current.CancellationToken));
        var bookNames = await dbContext.Products
            .Where(p => p.CategoryId == booksId)
            .Select(p => p.ProductName)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, bookNames.Count);
        Assert.Single(bookNames, name => name == "Domain-Driven Design");
    }

    [Fact]
    public async Task StartAsync_ShouldLeaveCatalogEmpty_WhenSeedSampleDataIsDisabled()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync(TestContext.Current.CancellationToken);

        await RunInitializerAsync(connectionString, seedSampleData: false);
        await RunInitializerAsync(connectionString, seedSampleData: false);

        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        Assert.Equal(0, await dbContext.Categories.CountAsync(TestContext.Current.CancellationToken));
    }

    private static async Task RunInitializerAsync(string connectionString, bool seedSampleData)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DatabaseInitializer.SeedSampleDataKey] = seedSampleData.ToString(),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
        await using var provider = services.BuildServiceProvider();

        var initializer = new DatabaseInitializer(
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            NullLogger<DatabaseInitializer>.Instance);

        await initializer.StartAsync(TestContext.Current.CancellationToken);
    }
}
