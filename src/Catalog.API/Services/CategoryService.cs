using Catalog.API.Data;
using Catalog.API.Dtos;
using Catalog.API.Models;
using Common.Lib.Dtos;
using Common.Lib.Exceptions;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Catalog.API.Services;

public sealed class CategoryService(AppDbContext dbContext, ILogger<CategoryService> logger) : ICategoryService
{
    public Task<PageResponse<CategoryResponse>> GetPageAsync(PageRequest request, CancellationToken cancellationToken) =>
        dbContext.Categories
            .AsNoTracking()
            .OrderBy(c => c.CategoryName)
            .ThenBy(c => c.Id)
            .ProjectToType<CategoryResponse>()
            .ToPageAsync(request, cancellationToken);

    public async Task<CategoryResponse> CreateAsync(CreateCategoryRequest request, CancellationToken cancellationToken)
    {
        var categoryName = request.CategoryName.Trim();

        if (await dbContext.Categories.AnyAsync(c => c.CategoryName == categoryName, cancellationToken))
        {
            throw DuplicateName(categoryName);
        }

        var category = new Category { CategoryName = categoryName };
        dbContext.Categories.Add(category);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        })
        {
            // Another request inserted the same name between the check above and this insert:
            // the unique index is the final arbiter.
            throw DuplicateName(categoryName, exception);
        }

        logger.LogInformation("Created category {CategoryId} {CategoryName}", category.Id, category.CategoryName);
        return category.Adapt<CategoryResponse>();
    }

    private static BusinessRuleException DuplicateName(string categoryName, Exception? innerException = null)
    {
        var message = $"A category named '{categoryName}' already exists.";
        return innerException is null
            ? new BusinessRuleException(message)
            : new BusinessRuleException(message, innerException);
    }
}
