using Customer.API.Data;
using FluentValidation;

namespace Customer.API.Validators;

/// <summary>
/// Field rules shared by the create and update validators. Lengths are measured after Trim,
/// on the value the service actually stores.
/// </summary>
internal static class CustomerRules
{
    public static IRuleBuilderOptions<T, string> TrimmedText<T>(this IRuleBuilder<T, string> rule, int maxLength) =>
        rule.NotEmpty()
            .Must(value => value.Trim().Length <= maxLength)
            .WithMessage($"{{PropertyName}} must be {maxLength} characters or fewer.");

    public static IRuleBuilderOptions<T, string> Telephone<T>(this IRuleBuilder<T, string> rule) =>
        rule.TrimmedText(CustomerLimits.TelephoneMaxLength)
            .Matches(@"^\s*\+?[0-9 ()\-.]{6,}\s*$")
            .WithMessage("{PropertyName} may only contain digits, spaces, parentheses, dots or dashes, with an optional leading +.");

    public static IRuleBuilderOptions<T, string> EmailAddressValue<T>(this IRuleBuilder<T, string> rule) =>
        rule.TrimmedText(CustomerLimits.EmailMaxLength).EmailAddress();
}
