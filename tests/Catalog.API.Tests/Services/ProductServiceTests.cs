using Catalog.API.Dtos;
using Catalog.API.Models;
using Catalog.API.Services;
using Catalog.API.Tests.TestSupport;
using Common.Lib.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Catalog.API.Tests.Services;

/// <summary>ProductService against a real, migrated PostgreSQL database (one per test).</summary>
public sealed class ProductServiceTests(PostgreSqlFixture postgres) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task GetPageAsync_ShouldReturnRequestedPageAndTotalCount_WhenCatalogHasMoreProductsThanPageSize()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        await SeedAsync(connectionString, ("Books", ["A", "B", "C", "D", "E"]));

        var page = await CreateService(connectionString).GetPageAsync(
            new ProductPageRequest { Page = 2, PageSize = 2 }, TestContext.Current.CancellationToken);

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(2, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Equal(["C", "D"], page.Items.Select(p => p.ProductName));
    }

    [Fact]
    public async Task GetPageAsync_ShouldReturnEmptyItemsWithRealTotal_WhenPageIsPastTheEnd()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        await SeedAsync(connectionString, ("Books", ["A", "B", "C"]));

        var page = await CreateService(connectionString).GetPageAsync(
            new ProductPageRequest { Page = 5, PageSize = 2 }, TestContext.Current.CancellationToken);

        Assert.Empty(page.Items);
        Assert.Equal(3, page.TotalCount);
    }

    [Fact]
    public async Task GetPageAsync_ShouldReturnOnlyThatCategoryProducts_WhenCategoryIdIsSet()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        var categoryIds = await SeedAsync(
            connectionString,
            ("Books", ["Book 1", "Book 2", "Book 3"]),
            ("Electronics", ["Keyboard", "Monitor"]));
        var electronicsId = categoryIds["Electronics"];

        var page = await CreateService(connectionString).GetPageAsync(
            new ProductPageRequest { CategoryId = electronicsId, PageSize = 10 }, TestContext.Current.CancellationToken);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(["Keyboard", "Monitor"], page.Items.Select(p => p.ProductName));
        Assert.All(page.Items, p =>
        {
            Assert.Equal(electronicsId, p.CategoryId);
            Assert.Equal("Electronics", p.CategoryName);
        });
    }

    [Fact]
    public async Task GetPageAsync_ShouldReturnEmptyPage_WhenCategoryIdMatchesNoCategory()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        await SeedAsync(connectionString, ("Books", ["Book 1"]));

        var page = await CreateService(connectionString).GetPageAsync(
            new ProductPageRequest { CategoryId = 999_999 }, TestContext.Current.CancellationToken);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task GetPageAsync_ShouldOrderByNameThenId_WhenProductsShareAName()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        // Inserted out of alphabetical order, with three identical names.
        await SeedAsync(connectionString, ("Books", ["Zeta", "Same", "Alpha", "Same", "Same"]));
        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        var expectedIds = await dbContext.Products
            .OrderBy(p => p.ProductName == "Alpha" ? 0 : p.ProductName == "Same" ? 1 : 2)
            .ThenBy(p => p.Id)
            .Select(p => p.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        var service = CreateService(connectionString);

        // Walking the pages one product at a time must visit every product exactly once, in order.
        var visited = new List<int>();
        for (var pageNumber = 1; pageNumber <= expectedIds.Count; pageNumber++)
        {
            var page = await service.GetPageAsync(
                new ProductPageRequest { Page = pageNumber, PageSize = 1 }, TestContext.Current.CancellationToken);
            visited.Add(Assert.Single(page.Items).Id);
        }

        Assert.Equal(expectedIds, visited);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnFlatProduct_WhenProductExists()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        var categoryIds = await SeedAsync(connectionString, ("Books", ["Building Microservices"]));
        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        var productId = await dbContext.Products.Select(p => p.Id).SingleAsync(TestContext.Current.CancellationToken);

        var product = await CreateService(connectionString).GetByIdAsync(productId, TestContext.Current.CancellationToken);

        Assert.Equal(
            new ProductResponse(productId, categoryIds["Books"], "Books", "Building Microservices", 10m),
            product);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldThrowResourceNotFoundException_WhenProductDoesNotExist()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => CreateService(connectionString).GetByIdAsync(999_999, TestContext.Current.CancellationToken));

        Assert.Contains("999999", exception.Message);
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowBusinessRuleException_WhenCategoryDoesNotExist()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<BusinessRuleException>(() => CreateService(connectionString).CreateAsync(
            new CreateProductRequest(999_999, "Orphan", 1m), TestContext.Current.CancellationToken));

        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        Assert.Equal(0, await dbContext.Products.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateAsync_ShouldPersistTrimmedProduct_WhenCategoryExists()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        var categoryIds = await SeedAsync(connectionString, ("Books", []));
        var booksId = categoryIds["Books"];

        var created = await CreateService(connectionString).CreateAsync(
            new CreateProductRequest(booksId, "  Refactoring  ", 39.90m), TestContext.Current.CancellationToken);

        Assert.True(created.Id > 0);
        Assert.Equal(new ProductResponse(created.Id, booksId, "Books", "Refactoring", 39.90m), created);
        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        var stored = await dbContext.Products.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal("Refactoring", stored.ProductName);
        Assert.Equal(booksId, stored.CategoryId);
        Assert.Equal(39.90m, stored.UnitPrice);
    }

    private static ProductService CreateService(string connectionString) =>
        new(PostgreSqlFixture.CreateDbContext(connectionString), NullLogger<ProductService>.Instance);

    /// <summary>Inserts categories and their products (unit price 10), in the given order.</summary>
    private static async Task<Dictionary<string, int>> SeedAsync(
        string connectionString,
        params (string CategoryName, string[] ProductNames)[] categories)
    {
        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        var ids = new Dictionary<string, int>();

        foreach (var (categoryName, productNames) in categories)
        {
            var category = new Category { CategoryName = categoryName };
            dbContext.Categories.Add(category);
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

            // One insert per product, so identity values follow the listed order.
            foreach (var productName in productNames)
            {
                dbContext.Products.Add(new Product { CategoryId = category.Id, ProductName = productName, UnitPrice = 10m });
                await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            ids[categoryName] = category.Id;
        }

        return ids;
    }
}
