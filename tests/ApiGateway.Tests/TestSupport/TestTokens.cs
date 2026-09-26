using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ApiGateway.Tests.TestSupport;

/// <summary>
/// Signs access tokens the way identity-api does (HS256, the configured issuer and audience, short
/// claim names), plus the invalid variants the gateway must reject.
/// </summary>
public static class TestTokens
{
    /// <summary>The key given to the gateway as Jwt:SigningKey (AppHost injects the real one).</summary>
    public const string SigningKey = "api-gateway-tests-signing-key-at-least-256-bits-long";

    /// <summary>A key of valid length that the gateway does not trust.</summary>
    public const string OtherSigningKey = "another-signing-key-the-gateway-never-trusts-256-bits";

    public static string CreateValid(IServiceProvider services) =>
        Create(Options(services), SigningKey, TimeSpan.FromMinutes(15));

    /// <summary>Expired well beyond the validation clock skew.</summary>
    public static string CreateExpired(IServiceProvider services) =>
        Create(Options(services), SigningKey, TimeSpan.FromMinutes(-10));

    public static string CreateSignedWithOtherKey(IServiceProvider services) =>
        Create(Options(services), OtherSigningKey, TimeSpan.FromMinutes(15));

    public static string CreateForOtherAudience(IServiceProvider services)
    {
        var jwt = Options(services);
        return Create(
            new JwtValidationOptions { Issuer = jwt.Issuer, Audience = "another-audience", SigningKey = jwt.SigningKey },
            SigningKey,
            TimeSpan.FromMinutes(15));
    }

    private static JwtValidationOptions Options(IServiceProvider services) =>
        services.GetRequiredService<IOptions<JwtValidationOptions>>().Value;

    private static string Create(JwtValidationOptions jwt, string signingKey, TimeSpan lifetime)
    {
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;
        var expires = now.Add(lifetime);
        var issuedAt = lifetime < TimeSpan.Zero ? expires.AddMinutes(-15) : now;

        var identity = new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, "42"),
            new Claim(AuthenticationExtensions.NameClaimType, "test-user"),
            new Claim(AuthenticationExtensions.RoleClaimType, "Customer"),
        ]);

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Subject = identity,
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = expires,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                SecurityAlgorithms.HmacSha256),
        });
    }
}
