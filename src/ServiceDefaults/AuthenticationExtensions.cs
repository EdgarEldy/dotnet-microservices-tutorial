using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// JWT validation shared by every business service (the dotnet/eShop convention keeps it in
/// ServiceDefaults). identity-api issues HS256 access tokens; each service validates issuer,
/// audience, lifetime and signature locally, with the key AppHost injects as Jwt__SigningKey,
/// without calling identity-api. The token blacklist is only checked by identity-api itself.
/// </summary>
public static class AuthenticationExtensions
{
    /// <summary>Claim carrying one RESOURCE:ACTION permission granted by the user's roles.</summary>
    public const string PermissionClaimType = "permission";

    public const string NameClaimType = "name";

    public const string RoleClaimType = "role";

    /// <summary>
    /// Registers JwtBearer as the default scheme, configured from the "Jwt" section, and an
    /// authorization policy provider turning any "RESOURCE:ACTION" policy name into a
    /// requirement on the "permission" claim, so <c>[Authorize(Policy = "CATALOG:WRITE")]</c>
    /// works without declaring each policy.
    /// </summary>
    public static IHostApplicationBuilder AddDefaultAuthentication(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        services.AddOptions<JwtValidationOptions>()
            .Bind(builder.Configuration.GetSection(JwtValidationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured from the bound options when first resolved (not read eagerly), so the key
        // AppHost injects, or a test's configuration, is the one used.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtValidationOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;

                // Keep the short JWT claim names ("sub", "name", "role", "permission").
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    NameClaimType = NameClaimType,
                    RoleClaimType = RoleClaimType,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        services.AddAuthorization();
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();

        return builder;
    }
}

/// <summary>
/// The "Jwt" configuration section as seen by a service that only validates tokens. The signing
/// key is AppHost's secret parameter "jwt-signing-key", never committed.
/// </summary>
public sealed class JwtValidationOptions
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
}

/// <summary>
/// Resolves explicitly registered policies first, then builds one on the fly for any policy name
/// in the RESOURCE:ACTION format: an authenticated user holding a "permission" claim with exactly
/// that value.
/// </summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        var policy = await base.GetPolicyAsync(policyName);
        if (policy is not null || !IsPermission(policyName))
        {
            return policy;
        }

        return new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(AuthenticationExtensions.PermissionClaimType, policyName)
            .Build();
    }

    private static bool IsPermission(string policyName)
    {
        var separator = policyName.IndexOf(':', StringComparison.Ordinal);
        return separator > 0 && separator < policyName.Length - 1;
    }
}
