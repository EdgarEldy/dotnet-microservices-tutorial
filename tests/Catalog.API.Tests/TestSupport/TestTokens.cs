using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Catalog.API.Tests.TestSupport;

/// <summary>
/// Signs access tokens the way identity-api does: HS256, the "Jwt" issuer and audience the
/// service is configured with, and the short claim names sub, name, role and permission.
/// </summary>
public static class TestTokens
{
    /// <summary>The key given to the service as Jwt:SigningKey (AppHost injects the real one).</summary>
    public const string SigningKey = "catalog-api-tests-signing-key-at-least-256-bits-long";

    public static string Create(IServiceProvider services, string role, params string[] permissions)
    {
        var jwt = services.GetRequiredService<IOptions<JwtValidationOptions>>().Value;
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;

        var identity = new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
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
