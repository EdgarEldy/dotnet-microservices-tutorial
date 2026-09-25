using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Identity.API.Security;

/// <summary>
/// The "Jwt" configuration section. <see cref="SigningKey"/> is the HMAC-SHA256 key shared with
/// every service that validates identity-api's tokens (api-gateway and the business services):
/// AppHost generates it once as the secret parameter "jwt-signing-key" and injects it as the
/// environment variable Jwt__SigningKey, so it is never committed. Outside AppHost, supply it
/// through user-secrets or the environment.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    // HS256 needs a key of at least 256 bits.
    private const int MinimumSigningKeyLength = 32;

    [Required]
    [MinLength(MinimumSigningKeyLength)]
    public string SigningKey { get; set; } = string.Empty;

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    [Range(typeof(TimeSpan), "00:05:00", "90.00:00:00")]
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);

    public SymmetricSecurityKey CreateSigningKey() => new(Encoding.UTF8.GetBytes(SigningKey));
}
