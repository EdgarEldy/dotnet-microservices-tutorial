namespace Identity.API.Dtos;

/// <summary>An access + refresh token pair, returned by Login and Refresh.</summary>
public record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    string TokenType = "Bearer");
