namespace Identity.API.Dtos;

/// <summary>Query string of GET /api/v1/Auth/ConfirmEmail, as sent in the activation e-mail.</summary>
public record ConfirmEmailRequest(int UserId, string Token);
