using Catalog.API.Dtos;
using Catalog.API.Models;
using Catalog.API.Services;
using Catalog.API.Tests.TestSupport;
using Common.Lib.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Catalog.API.Tests.Services;

/// <summary>CategoryService against a real, migrated PostgreSQL database (one per test).</summary>
public sealed class CategoryServiceTests(PostgreSqlFixture postgres) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task CreateAsync_ShouldPersistTrimmedName_WhenNameIsNew()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);

        var created = await CreateService(connectionString).CreateAsync(
            new CreateCategoryRequest("  Books  "), TestContext.Current.CancellationToken);

        Assert.True(created.Id > 0);
        Assert.Equal("Books", created.CategoryName);
        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        var stored = await dbContext.Categories.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal("Books", stored.CategoryName);
    }

    [Theory]
    [InlineData("Books")]
    [InlineData("  Books ")]
    public async Task CreateAsync_ShouldThrowBusinessRuleException_WhenCategoryNameAlreadyExists(string requestedName)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        await using (var arrange = PostgreSqlFixture.CreateDbContext(connectionString))
        {
            arrange.Categories.Add(new Category { CategoryName = "Books" });
            await arrange.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => CreateService(connectionString).CreateAsync(
            new CreateCategoryRequest(requestedName), TestContext.Current.CancellationToken));

        Assert.Contains("Books", exception.Message);
        await using var dbContext = PostgreSqlFixture.CreateDbContext(connectionString);
        Assert.Equal(1, await dbContext.Categories.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPageAsync_ShouldReturnCategoriesOrderedByName_WhenInsertedOutOfOrder()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        await using (var arrange = PostgreSqlFixture.CreateDbContext(connectionString))
        {
            foreach (var name in new[] { "Office Supplies", "Books", "Garden", "Electronics" })
            {
                arrange.Categories.Add(new Category { CategoryName = name });
                await arrange.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
        }

        var page = await CreateService(connectionString).GetPageAsync(
            new PageRequest { Page = 1, PageSize = 3 }, TestContext.Current.CancellationToken);

        Assert.Equal(4, page.TotalCount);
        Assert.Equal(["Books", "Electronics", "Garden"], page.Items.Select(c => c.CategoryName));
    }

    private static CategoryService CreateService(string connectionString) =>
        new(PostgreSqlFixture.CreateDbContext(connectionString), NullLogger<CategoryService>.Instance);
}
