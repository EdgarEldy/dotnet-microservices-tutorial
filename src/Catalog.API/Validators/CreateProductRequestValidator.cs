using Catalog.API.Data;
using Catalog.API.Dtos;
using FluentValidation;

namespace Catalog.API.Validators;

/// <summary>
/// Shape only. Whether the category exists is a business rule checked by ProductService (422),
/// not an input error.
/// </summary>
public sealed class CreateProductRequestValidator : AbstractValidator<CreateProductRequest>
{
    public CreateProductRequestValidator()
    {
        RuleFor(r => r.CategoryId).GreaterThan(0);
        // Length measured after Trim, on the value ProductService actually stores.
        RuleFor(r => r.ProductName)
            .NotEmpty()
            .Must(name => name.Trim().Length <= CatalogLimits.ProductNameMaxLength)
            .WithMessage($"{{PropertyName}} must be {CatalogLimits.ProductNameMaxLength} characters or fewer.");
        RuleFor(r => r.UnitPrice)
            .GreaterThan(0)
            .PrecisionScale(CatalogLimits.UnitPricePrecision, CatalogLimits.UnitPriceScale, ignoreTrailingZeros: true);
    }
}
