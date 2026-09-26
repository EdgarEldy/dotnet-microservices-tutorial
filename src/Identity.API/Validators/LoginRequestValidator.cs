using FluentValidation;
using Identity.API.Dtos;

namespace Identity.API.Validators;

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        // Presence only: stricter rules here would tell a caller something about stored accounts.
        RuleFor(r => r.Email).NotEmpty().MaximumLength(ValidationLimits.EmailMaxLength);
        RuleFor(r => r.Password).NotEmpty().MaximumLength(ValidationLimits.PasswordMaxLength);
    }
}
