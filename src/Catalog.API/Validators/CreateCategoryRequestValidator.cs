using Catalog.API.Data;
using Catalog.API.Dtos;
using FluentValidation;

namespace Catalog.API.Validators;

public sealed class CreateCategoryRequestValidator : AbstractValidator<CreateCategoryRequest>
{
    public CreateCategoryRequestValidator()
    {
        // Length measured after Trim, on the value CategoryService actually stores.
        RuleFor(r => r.CategoryName)
            .NotEmpty()
            .Must(name => name.Trim().Length <= CatalogLimits.CategoryNameMaxLength)
            .WithMessage($"{{PropertyName}} must be {CatalogLimits.CategoryNameMaxLength} characters or fewer.");
    }
}
