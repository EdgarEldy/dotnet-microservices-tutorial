using System.Security.Claims;
using Identity.API.Models;
using Identity.API.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Identity.API.Security;

/// <summary>
/// ASP.NET Core Identity extension point: on top of Identity's own claims (sub, name, email,
/// roles), adds one "permission" claim per distinct RESOURCE:ACTION granted by the user's roles.
/// The permissions travel inside the access token, so downstream permission checks never need
/// a database call. The permission lookup itself is delegated to <see cref="IRoleService"/>.
/// </summary>
public sealed class AppClaimsPrincipalFactory(
    UserManager<AppUser> userManager,
    RoleManager<AppRole> roleManager,
    IOptions<IdentityOptions> options,
    IRoleService roleService)
    : UserClaimsPrincipalFactory<AppUser, AppRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        var roles = identity.FindAll(Options.ClaimsIdentity.RoleClaimType).Select(claim => claim.Value);

        // IUserClaimsPrincipalFactory.CreateAsync takes no CancellationToken: none to flow here.
        var permissions = await roleService.GetPermissionsAsync(roles, CancellationToken.None);

        identity.AddClaims(permissions.Select(permission => new Claim(AppClaimTypes.Permission, permission)));

        return identity;
    }
}
