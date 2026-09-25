namespace Identity.API.Dtos;

public record ResetPasswordRequest(string Email, string Token, string NewPassword);
