using Catalog.API.Dtos;
using Common.Lib.Dtos;

namespace Catalog.API.Services;

public interface IProductService
{
    /// <summary>Products ordered by name, restricted to one category when CategoryId is set.</summary>
    Task<PageResponse<ProductResponse>> GetPageAsync(ProductPageRequest request, CancellationToken cancellationToken);

    /// <summary>Throws a ResourceNotFoundException when the product does not exist.</summary>
    Task<ProductResponse> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>Throws a BusinessRuleException when the category does not exist.</summary>
    Task<ProductResponse> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken);
}
