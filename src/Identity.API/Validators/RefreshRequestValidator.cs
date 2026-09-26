using FluentValidation;
using Identity.API.Dtos;

namespace Identity.API.Validators;

public sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(r => r.RefreshToken).NotEmpty().MaximumLength(ValidationLimits.TokenMaxLength);
    }
}
