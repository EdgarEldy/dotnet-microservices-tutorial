using Catalog.API.Models;
using Microsoft.EntityFrameworkCore;

namespace Catalog.API.Data;

/// <summary>
/// A few sample categories and products, so the catalog is usable in the demo as soon as the
/// system starts (an order needs an existing product). Idempotent: a category is matched by its
/// name, a product by its name within its category, and only what is missing is inserted.
/// </summary>
public static class CatalogSeeder
{
    private static readonly IReadOnlyDictionary<string, (string ProductName, decimal UnitPrice)[]> SampleData =
        new Dictionary<string, (string, decimal)[]>
        {
            ["Books"] =
            [
                ("Domain-Driven Design", 54.99m),
                ("Building Microservices", 49.90m),
            ],
            ["Electronics"] =
            [
                ("Mechanical Keyboard", 89.00m),
                ("27-inch Monitor", 229.50m),
            ],
            ["Office Supplies"] =
            [
                ("Notebook A5", 4.25m),
                ("Ballpoint Pen Pack", 6.80m),
            ],
        };

    public static async Task SeedAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var categoryNames = SampleData.Keys.ToArray();
        var categories = await dbContext.Categories
            .Include(c => c.Products)
            .Where(c => categoryNames.Contains(c.CategoryName))
            .ToDictionaryAsync(c => c.CategoryName, StringComparer.Ordinal, cancellationToken);

        foreach (var (categoryName, products) in SampleData)
        {
            if (!categories.TryGetValue(categoryName, out var category))
            {
                category = new Category { CategoryName = categoryName };
                dbContext.Categories.Add(category);
            }

            foreach (var (productName, unitPrice) in products)
            {
                if (!category.Products.Any(p => p.ProductName == productName))
                {
                    category.Products.Add(new Product { ProductName = productName, UnitPrice = unitPrice });
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
