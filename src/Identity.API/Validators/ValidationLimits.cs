namespace Identity.API.Validators;

/// <summary>
/// Input limits shared by the validators. The password rules beyond the length (digit, upper
/// and lower case, symbol) are enforced by ASP.NET Core Identity and returned the same way.
/// </summary>
public static class ValidationLimits
{
    public const int EmailMaxLength = 256;
    public const int PasswordMinLength = 8;
    public const int PasswordMaxLength = 128;
    public const int TokenMaxLength = 2048;
}
