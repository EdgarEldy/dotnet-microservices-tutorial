using Catalog.API.Data;
using Catalog.API.Models;
using Catalog.API.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PactNet;
using PactNet.Verifier;

namespace Catalog.API.Tests.PactTests;

/// <summary>
/// Replays every interaction of the committed pacts/order-api-catalog-api.json against the real
/// catalog-api (full pipeline, JWT validation included, PostgreSQL behind it). Renaming, removing
/// or retyping a ProductResponse member, or changing the status/content type, fails this test.
/// </summary>
public sealed class OrderApiPactVerificationTests(CatalogApiFactory factory) : IClassFixture<CatalogApiFactory>
{
    private const int ExistingProductId = 1;
    private const int MissingProductId = 999;

    [Fact]
    public async Task GetProduct_ShouldSatisfyTheOrderApiPact_WhenVerifiedAgainstTheCommittedContract()
    {
        var (server, address) = PactProvider.Start(factory, new Dictionary<string, Func<IServiceProvider, CancellationToken, Task>>
        {
            ["a product with id 1 exists"] = EnsureProductExistsAsync,
            ["no product with id 999"] = EnsureProductIsMissingAsync,
        });
        await using var _ = server;

        // order-api forwards its caller's token: any authenticated user may read a product.
        var token = TestTokens.Create(server.Services, CatalogApiFactory.CustomerRole);

        using var verifier = new PactVerifier("catalog-api", new PactVerifierConfig
        {
            LogLevel = PactLogLevel.Warn,
            Outputters = [new PactProvider.XunitOutput()],
        });

        verifier
            .WithHttpEndpoint(address)
            .WithFileSource(new FileInfo(PactProvider.PactFile("order-api", "catalog-api")))
            .WithProviderStateUrl(new Uri(address, PactProvider.ProviderStatesPath))
            .WithCustomHeader("Authorization", $"Bearer {token}")
            .Verify();
    }

    private static async Task EnsureProductExistsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var dbContext = services.GetRequiredService<AppDbContext>();
        if (await dbContext.Products.AnyAsync(p => p.Id == ExistingProductId, cancellationToken))
        {
            return;
        }

        var category = new Category { CategoryName = "Beverages" };
        dbContext.Products.Add(new Product
        {
            Id = ExistingProductId,
            Category = category,
            ProductName = "Espresso",
            UnitPrice = 2.50m,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureProductIsMissingAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        await services.GetRequiredService<AppDbContext>().Products
            .Where(p => p.Id == MissingProductId)
            .ExecuteDeleteAsync(cancellationToken);
}
