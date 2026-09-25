using Catalog.API.Dtos;
using Common.Lib.Dtos;

namespace Catalog.API.Services;

public interface ICategoryService
{
    /// <summary>Categories ordered by name.</summary>
    Task<PageResponse<CategoryResponse>> GetPageAsync(PageRequest request, CancellationToken cancellationToken);

    /// <summary>Throws a BusinessRuleException when a category with the same name already exists.</summary>
    Task<CategoryResponse> CreateAsync(CreateCategoryRequest request, CancellationToken cancellationToken);
}
