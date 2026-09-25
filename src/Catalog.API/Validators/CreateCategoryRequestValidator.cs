using Catalog.API.Data;
using Catalog.API.Dtos;
using FluentValidation;

namespace Catalog.API.Validators;

public sealed class CreateCategoryRequestValidator : AbstractValidator<CreateCategoryRequest>
{
    public CreateCategoryRequestValidator()
    {
        RuleFor(r => r.CategoryName).NotEmpty().MaximumLength(CatalogLimits.CategoryNameMaxLength);
    }
}
