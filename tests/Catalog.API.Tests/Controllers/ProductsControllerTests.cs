using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Catalog.API.Dtos;
using Catalog.API.Models;
using Catalog.API.Tests.TestSupport;
using Common.Lib.Dtos;

namespace Catalog.API.Tests.Controllers;

/// <summary>
/// /api/v1/Catalog/Products through the real pipeline (JWT, validation filter, ProblemDetails,
/// pagination headers) against a real PostgreSQL database.
/// </summary>
public sealed class ProductsControllerTests(CatalogApiFactory factory) : IClassFixture<CatalogApiFactory>
{
    private const string ProductsUrl = "/api/v1/Catalog/Products";

    [Fact]
    public async Task GetProducts_ShouldReturnUnauthorizedProblem_WhenNoTokenIsSent()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(ProductsUrl, TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetProducts_ShouldReturnBareArrayWithPaginationHeaders_WhenCategoryHasMoreProductsThanPageSize()
    {
        var categoryId = await CreateCategoryWithProductsAsync("Paged", "P1", "P2", "P3");
        using var client = factory.CreateCustomerClient();

        using var response = await client.GetAsync(
            $"{ProductsUrl}?categoryId={categoryId}&page=1&pageSize=2", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("3", Assert.Single(response.Headers.GetValues(PageResponse<ProductResponse>.TotalCountHeader)));

        var link = string.Join(", ", response.Headers.GetValues(PageResponse<ProductResponse>.LinkHeader));
        Assert.Contains($"<{ProductsUrl}?categoryId={categoryId}&page=1&pageSize=2>; rel=\"first\"", link);
        Assert.Contains($"<{ProductsUrl}?categoryId={categoryId}&page=2&pageSize=2>; rel=\"next\"", link);
        Assert.Contains($"<{ProductsUrl}?categoryId={categoryId}&page=2&pageSize=2>; rel=\"last\"", link);
        Assert.DoesNotContain("rel=\"prev\"", link);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        Assert.Equal(
            ["P1", "P2"],
            body.RootElement.EnumerateArray().Select(p => p.GetProperty("productName").GetString()));
    }

    [Theory]
    [InlineData("pageSize=0", "PageSize")]
    [InlineData("pageSize=101", "PageSize")]
    [InlineData("page=0", "Page")]
    [InlineData("categoryId=0", "CategoryId")]
    public async Task GetProducts_ShouldReturnValidationProblem_WhenQueryIsOutOfRange(string query, string invalidProperty)
    {
        using var client = factory.CreateCustomerClient();

        using var response = await client.GetAsync($"{ProductsUrl}?{query}", TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertValidationProblemAsync(response, invalidProperty);
    }

    [Fact]
    public async Task GetProduct_ShouldReturnExactFlatShape_WhenProductExists()
    {
        // The contract order-api's Refit client depends on: exactly these five camelCase
        // properties, with these JSON types, and nothing else (no envelope, no nested category).
        var categoryId = await CreateCategoryWithProductsAsync("Shape");
        var productId = await factory.WithDbContextAsync(async dbContext =>
        {
            var product = new Product { CategoryId = categoryId, ProductName = "Shape Product", UnitPrice = 12.34m };
            dbContext.Products.Add(product);
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            return product.Id;
        });
        using var client = factory.CreateCustomerClient();

        using var response = await client.GetAsync($"{ProductsUrl}/{productId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = body.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        var properties = root.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind);
        Assert.Equal(
            new Dictionary<string, JsonValueKind>
            {
                ["id"] = JsonValueKind.Number,
                ["categoryId"] = JsonValueKind.Number,
                ["categoryName"] = JsonValueKind.String,
                ["productName"] = JsonValueKind.String,
                ["unitPrice"] = JsonValueKind.Number,
            },
            properties);

        Assert.Equal(productId, root.GetProperty("id").GetInt32());
        Assert.Equal(categoryId, root.GetProperty("categoryId").GetInt32());
        Assert.Equal("Shape", root.GetProperty("categoryName").GetString());
        Assert.Equal("Shape Product", root.GetProperty("productName").GetString());
        Assert.Equal(12.34m, root.GetProperty("unitPrice").GetDecimal());
    }

    [Fact]
    public async Task GetProduct_ShouldReturnNotFoundProblem_WhenProductDoesNotExist()
    {
        using var client = factory.CreateCustomerClient();

        using var response = await client.GetAsync($"{ProductsUrl}/999999", TestContext.Current.CancellationToken);

        var problem = await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.NotFound);
        Assert.Contains("999999", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task GetProduct_ShouldReturnUnauthorizedProblem_WhenNoTokenIsSent()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{ProductsUrl}/1", TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateProduct_ShouldReturnUnauthorizedProblem_WhenNoTokenIsSent()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            ProductsUrl, new CreateProductRequest(1, "Anonymous", 1m), TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateProduct_ShouldReturnForbiddenProblem_WhenTokenLacksWritePermission()
    {
        var categoryId = await CreateCategoryWithProductsAsync("Forbidden");
        using var client = factory.CreateCustomerClient();

        using var response = await client.PostAsJsonAsync(
            ProductsUrl, new CreateProductRequest(categoryId, "Not Allowed", 1m), TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal(0, await factory.WithDbContextAsync(dbContext =>
            Task.FromResult(dbContext.Products.Count(p => p.CategoryId == categoryId))));
    }

    [Fact]
    public async Task CreateProduct_ShouldReturnForbiddenProblem_WhenTokenHasAdminRoleButNotWritePermission()
    {
        // Authorization is by permission claim, not by role name.
        var categoryId = await CreateCategoryWithProductsAsync("AdminWithoutPermission");
        using var client = factory.CreateClientWithToken(CatalogApiFactory.AdminRole, "CATALOG:READ");

        using var response = await client.PostAsJsonAsync(
            ProductsUrl, new CreateProductRequest(categoryId, "Not Allowed", 1m), TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateProduct_ShouldReturnCreatedWithLocation_WhenTokenHasWritePermission()
    {
        var categoryId = await CreateCategoryWithProductsAsync("Created");
        using var client = factory.CreateAdminClient();

        using var response = await client.PostAsJsonAsync(
            ProductsUrl, new CreateProductRequest(categoryId, "New Product", 19.99m), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ProductResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.Equal(new ProductResponse(created.Id, categoryId, "Created", "New Product", 19.99m), created);

        Assert.NotNull(response.Headers.Location);
        Assert.Equal($"{ProductsUrl}/{created.Id}", response.Headers.Location.IsAbsoluteUri
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location.OriginalString);

        using var fetched = await client.GetAsync(response.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal(created, await fetched.Content.ReadFromJsonAsync<ProductResponse>(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateProduct_ShouldReturnUnprocessableEntityProblem_WhenCategoryDoesNotExist()
    {
        using var client = factory.CreateAdminClient();

        using var response = await client.PostAsJsonAsync(
            ProductsUrl, new CreateProductRequest(999_999, "Orphan", 1m), TestContext.Current.CancellationToken);

        var problem = await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Contains("999999", problem.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData(0, "Valid", 1, "CategoryId")]
    [InlineData(1, "", 1, "ProductName")]
    [InlineData(1, "Valid", 0, "UnitPrice")]
    [InlineData(1, "Valid", 1.234, "UnitPrice")]
    public async Task CreateProduct_ShouldReturnValidationProblem_WhenBodyIsInvalid(
        int categoryId, string productName, double unitPrice, string invalidProperty)
    {
        using var client = factory.CreateAdminClient();

        using var response = await client.PostAsJsonAsync(
            ProductsUrl,
            new CreateProductRequest(categoryId, productName, (decimal)unitPrice),
            TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertValidationProblemAsync(response, invalidProperty);
    }

    /// <summary>Inserts a category (unique name per test) and its products directly in catalog-db.</summary>
    private Task<int> CreateCategoryWithProductsAsync(string categoryName, params string[] productNames) =>
        factory.WithDbContextAsync(async dbContext =>
        {
            var category = new Category { CategoryName = categoryName };
            foreach (var productName in productNames)
            {
                category.Products.Add(new Product { ProductName = productName, UnitPrice = 5m });
            }

            dbContext.Categories.Add(category);
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            return category.Id;
        });
}
