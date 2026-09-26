using Order.API.Dtos;
using FluentValidation;

namespace Order.API.Validators;

public sealed class PageRequestValidator : AbstractValidator<PageRequest>
{
    public PageRequestValidator()
    {
        RuleFor(r => r.Page).GreaterThanOrEqualTo(1);
        RuleFor(r => r.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize);
    }
}
