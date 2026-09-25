using FluentValidation;
using Identity.API.Dtos;

namespace Identity.API.Validators;

public sealed class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator()
    {
        RuleFor(r => r.Email).NotEmpty().EmailAddress().MaximumLength(ValidationLimits.EmailMaxLength);
    }
}
