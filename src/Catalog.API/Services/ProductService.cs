using Catalog.API.Data;
using Catalog.API.Dtos;
using Catalog.API.Models;
using Common.Lib.Dtos;
using Common.Lib.Exceptions;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Catalog.API.Services;

public sealed class ProductService(AppDbContext dbContext, ILogger<ProductService> logger) : IProductService
{
    public Task<PageResponse<ProductResponse>> GetPageAsync(ProductPageRequest request, CancellationToken cancellationToken)
    {
        var products = dbContext.Products.AsNoTracking();

        if (request.CategoryId is { } categoryId)
        {
            products = products.Where(p => p.CategoryId == categoryId);
        }

        return products
            .OrderBy(p => p.ProductName)
            .ThenBy(p => p.Id)
            .ProjectToType<ProductResponse>()
            .ToPageAsync(request, cancellationToken);
    }

    public async Task<ProductResponse> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        await dbContext.Products
            .AsNoTracking()
            .Where(p => p.Id == id)
            .ProjectToType<ProductResponse>()
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new ResourceNotFoundException(nameof(Product), id);

    public async Task<ProductResponse> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken)
    {
        // The request is well-formed (validated), but it may name a category that does not exist:
        // a business rule (422), not a missing resource of this endpoint (404).
        var category = await dbContext.Categories
            .FirstOrDefaultAsync(c => c.Id == request.CategoryId, cancellationToken)
            ?? throw new BusinessRuleException($"Category with id '{request.CategoryId}' does not exist.");

        var product = new Product
        {
            Category = category,
            ProductName = request.ProductName.Trim(),
            UnitPrice = request.UnitPrice,
        };
        dbContext.Products.Add(product);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Created product {ProductId} in category {CategoryId}", product.Id, category.Id);
        return product.Adapt<ProductResponse>();
    }
}
