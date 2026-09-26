using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Order.API.ResilienceTests.TestSupport;

/// <summary>
/// Signs access tokens the way identity-api does: HS256, the "Jwt" issuer and audience order-api is
/// configured with, and the short claim names sub, name, role and permission.
/// </summary>
public static class TestTokens
{
    /// <summary>The key given to the service as Jwt:SigningKey (AppHost injects the real one).</summary>
    public const string SigningKey = "order-api-resilience-tests-signing-key-0123456789";

    public static string Create(IServiceProvider services, int userId, string role, params string[] permissions)
    {
        var jwt = services.GetRequiredService<IOptions<JwtValidationOptions>>().Value;
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;

        var identity = new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString(CultureInfo.InvariantCulture)),
            new Claim(AuthenticationExtensions.NameClaimType, "test-user"),
            new Claim(AuthenticationExtensions.RoleClaimType, role),
        ]);
        identity.AddClaims(permissions.Select(p => new Claim(AuthenticationExtensions.PermissionClaimType, p)));

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Subject = identity,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(15),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                SecurityAlgorithms.HmacSha256),
        });
    }
}
