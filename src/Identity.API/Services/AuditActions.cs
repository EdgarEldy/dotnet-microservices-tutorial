namespace Identity.API.Services;

/// <summary>
/// Values of audit_logs.action and audit_logs.entity_type written by the services.
/// </summary>
public static class AuditActions
{
    public const string UserRegistered = "UserRegistered";
    public const string EmailConfirmed = "EmailConfirmed";
    public const string LoginSucceeded = "LoginSucceeded";
    public const string LoginFailed = "LoginFailed";
    public const string RefreshTokenReuseDetected = "RefreshTokenReuseDetected";
    public const string RefreshTokenFamilyRevoked = "RefreshTokenFamilyRevoked";
    public const string LoggedOut = "LoggedOut";
    public const string PasswordResetRequested = "PasswordResetRequested";
    public const string PasswordReset = "PasswordReset";

    public const string UserEntity = "User";
    public const string RefreshTokenEntity = "RefreshToken";
}
