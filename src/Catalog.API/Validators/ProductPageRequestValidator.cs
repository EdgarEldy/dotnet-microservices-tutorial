using Catalog.API.Dtos;
using FluentValidation;

namespace Catalog.API.Validators;

public sealed class ProductPageRequestValidator : AbstractValidator<ProductPageRequest>
{
    public ProductPageRequestValidator()
    {
        Include(new PageRequestValidator());
        RuleFor(r => r.CategoryId).GreaterThan(0).When(r => r.CategoryId.HasValue);
    }
}
