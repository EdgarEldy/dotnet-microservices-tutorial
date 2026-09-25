using FluentValidation;
using Identity.API.Dtos;

namespace Identity.API.Validators;

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(r => r.Email).NotEmpty().EmailAddress().MaximumLength(ValidationLimits.EmailMaxLength);
        RuleFor(r => r.Token).NotEmpty().MaximumLength(ValidationLimits.TokenMaxLength);
        RuleFor(r => r.NewPassword).NotEmpty()
            .MinimumLength(ValidationLimits.PasswordMinLength)
            .MaximumLength(ValidationLimits.PasswordMaxLength);
    }
}
