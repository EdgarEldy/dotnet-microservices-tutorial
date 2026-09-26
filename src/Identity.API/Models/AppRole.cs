using Microsoft.AspNetCore.Identity;

namespace Identity.API.Models;

/// <summary>
/// A role (AspNetRoles), granted a set of <see cref="Permission"/>s through <see cref="RolePermission"/>.
/// </summary>
public class AppRole : IdentityRole<int>
{
    public AppRole()
    {
    }

    public AppRole(string roleName)
        : base(roleName)
    {
    }

    public ICollection<RolePermission> RolePermissions { get; set; } = [];
}
