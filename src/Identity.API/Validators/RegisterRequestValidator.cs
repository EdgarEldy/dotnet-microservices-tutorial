using FluentValidation;
using Identity.API.Dtos;

namespace Identity.API.Validators;

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(r => r.Email).NotEmpty().EmailAddress().MaximumLength(ValidationLimits.EmailMaxLength);
        RuleFor(r => r.Password).NotEmpty()
            .MinimumLength(ValidationLimits.PasswordMinLength)
            .MaximumLength(ValidationLimits.PasswordMaxLength);
    }
}
