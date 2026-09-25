using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Catalog.API.Data;
using Catalog.API.Dtos;
using Catalog.API.Models;
using Catalog.API.Tests.TestSupport;
using Common.Lib.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Catalog.API.Tests.Controllers;

/// <summary>
/// /api/v1/Catalog/Categories through the real pipeline against a real PostgreSQL database.
/// </summary>
public sealed class CategoriesControllerTests(CatalogApiFactory factory) : IClassFixture<CatalogApiFactory>
{
    private const string CategoriesUrl = "/api/v1/Catalog/Categories";

    [Fact]
    public async Task GetCategories_ShouldReturnUnauthorizedProblem_WhenNoTokenIsSent()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(CategoriesUrl, TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetCategories_ShouldReturnBareArrayWithPaginationHeaders_WhenUserIsAuthenticated()
    {
        await factory.WithDbContextAsync(async dbContext =>
        {
            dbContext.Categories.AddRange(
                new Category { CategoryName = $"List {Guid.NewGuid():N}" },
                new Category { CategoryName = $"List {Guid.NewGuid():N}" });
            return await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        var expectedTotal = await factory.WithDbContextAsync(
            dbContext => dbContext.Categories.CountAsync(TestContext.Current.CancellationToken));
        using var client = factory.CreateCustomerClient();

        using var response = await client.GetAsync($"{CategoriesUrl}?pageSize=1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            expectedTotal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Assert.Single(response.Headers.GetValues(PageResponse<CategoryResponse>.TotalCountHeader)));
        var link = string.Join(", ", response.Headers.GetValues(PageResponse<CategoryResponse>.LinkHeader));
        Assert.Contains($"<{CategoriesUrl}?page=2&pageSize=1>; rel=\"next\"", link);
        Assert.Contains($"<{CategoriesUrl}?page={expectedTotal}&pageSize=1>; rel=\"last\"", link);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        var category = Assert.Single(body.RootElement.EnumerateArray());
        Assert.Equal(["id", "categoryName"], category.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData("pageSize=0", "PageSize")]
    [InlineData("pageSize=101", "PageSize")]
    [InlineData("page=0", "Page")]
    public async Task GetCategories_ShouldReturnValidationProblem_WhenQueryIsOutOfRange(string query, string invalidProperty)
    {
        using var client = factory.CreateCustomerClient();

        using var response = await client.GetAsync($"{CategoriesUrl}?{query}", TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertValidationProblemAsync(response, invalidProperty);
    }

    [Fact]
    public async Task CreateCategory_ShouldReturnUnauthorizedProblem_WhenNoTokenIsSent()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            CategoriesUrl, new CreateCategoryRequest("Anonymous"), TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateCategory_ShouldReturnForbiddenProblem_WhenTokenLacksWritePermission()
    {
        var name = $"Forbidden {Guid.NewGuid():N}";
        using var client = factory.CreateCustomerClient();

        using var response = await client.PostAsJsonAsync(
            CategoriesUrl, new CreateCategoryRequest(name), TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.Forbidden);
        Assert.False(await factory.WithDbContextAsync(dbContext =>
            dbContext.Categories.AnyAsync(c => c.CategoryName == name, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task CreateCategory_ShouldReturnCreatedCategory_WhenTokenHasWritePermission()
    {
        var name = $"Created {Guid.NewGuid():N}";
        using var client = factory.CreateAdminClient();

        using var response = await client.PostAsJsonAsync(
            CategoriesUrl, new CreateCategoryRequest(name), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var created = await response.Content.ReadFromJsonAsync<CategoryResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.True(created.Id > 0);
        Assert.Equal(name, created.CategoryName);
        Assert.True(await factory.WithDbContextAsync(dbContext =>
            dbContext.Categories.AnyAsync(c => c.Id == created.Id && c.CategoryName == name, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task CreateCategory_ShouldReturnUnprocessableEntityProblem_WhenNameAlreadyExists()
    {
        var name = $"Duplicate {Guid.NewGuid():N}";
        using var client = factory.CreateAdminClient();
        using var first = await client.PostAsJsonAsync(
            CategoriesUrl, new CreateCategoryRequest(name), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var response = await client.PostAsJsonAsync(
            CategoriesUrl, new CreateCategoryRequest(name), TestContext.Current.CancellationToken);

        var problem = await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Contains(name, problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task CreateCategory_ShouldReturnCreatedWithTrimmedName_WhenOnlyPaddingExceedsMaxLength()
    {
        // The service stores the trimmed name: surrounding spaces must not count against the limit.
        var name = $"Electronics {Guid.NewGuid():N}";
        var padded = name + new string(' ', 95);
        Assert.True(padded.Length > CatalogLimits.CategoryNameMaxLength);
        using var client = factory.CreateAdminClient();

        using var response = await client.PostAsJsonAsync(
            CategoriesUrl, new CreateCategoryRequest(padded), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CategoryResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.Equal(name, created.CategoryName);
        Assert.True(await factory.WithDbContextAsync(dbContext =>
            dbContext.Categories.AnyAsync(c => c.Id == created.Id && c.CategoryName == name, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task CreateCategory_ShouldReturnValidationProblem_WhenTrimmedNameExceedsMaxLength()
    {
        var name = "  " + new string('c', CatalogLimits.CategoryNameMaxLength + 1) + "  ";
        using var client = factory.CreateAdminClient();

        using var response = await client.PostAsJsonAsync(
            CategoriesUrl, new CreateCategoryRequest(name), TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertValidationProblemAsync(response, "CategoryName");
    }

    [Fact]
    public async Task CreateCategory_ShouldReturnValidationProblem_WhenNameIsEmpty()
    {
        using var client = factory.CreateAdminClient();

        using var response = await client.PostAsJsonAsync(
            CategoriesUrl, new CreateCategoryRequest(""), TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertValidationProblemAsync(response, "CategoryName");
    }
}
