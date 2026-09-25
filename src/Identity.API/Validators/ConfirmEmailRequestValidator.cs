using FluentValidation;
using Identity.API.Dtos;

namespace Identity.API.Validators;

public sealed class ConfirmEmailRequestValidator : AbstractValidator<ConfirmEmailRequest>
{
    public ConfirmEmailRequestValidator()
    {
        RuleFor(r => r.UserId).GreaterThan(0);
        RuleFor(r => r.Token).NotEmpty().MaximumLength(ValidationLimits.TokenMaxLength);
    }
}
