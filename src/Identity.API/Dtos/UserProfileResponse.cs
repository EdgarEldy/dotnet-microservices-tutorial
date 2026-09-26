namespace Identity.API.Dtos;

/// <summary>The current user, returned by GET /api/v1/Auth/Me.</summary>
public record UserProfileResponse(
    int Id,
    string Email,
    string UserName,
    bool EmailConfirmed,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);
