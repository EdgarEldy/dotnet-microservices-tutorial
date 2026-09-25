using Identity.API.Data;
using Identity.API.Models;
using Identity.API.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Identity.API.Services;

public sealed class RoleService(
    AppDbContext dbContext,
    RoleManager<AppRole> roleManager,
    ILogger<RoleService> logger) : IRoleService
{
    public async Task<IReadOnlyList<string>> GetPermissionsAsync(
        IEnumerable<string> roleNames,
        CancellationToken cancellationToken)
    {
        var names = roleNames.Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
        {
            return [];
        }

        var permissions = await dbContext.RolePermissions
            .AsNoTracking()
            .Where(rp => names.Contains(rp.Role.Name!))
            .Select(rp => rp.Permission.Resource + ":" + rp.Permission.Action)
            .Distinct()
            .ToListAsync(cancellationToken);

        return permissions.Order(StringComparer.Ordinal).ToList();
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var permissionsByName = await SeedPermissionsAsync(cancellationToken);

        foreach (var (roleName, rolePermissions) in AppRoles.PermissionsByRole)
        {
            var role = await roleManager.FindByNameAsync(roleName);
            if (role is null)
            {
                role = new AppRole(roleName);
                ThrowIfFailed(await roleManager.CreateAsync(role), roleName);
                logger.LogInformation("Seeded role {Role}", roleName);
            }

            var granted = await dbContext.RolePermissions
                .Where(rp => rp.RoleId == role.Id)
                .Select(rp => rp.PermissionId)
                .ToListAsync(cancellationToken);

            foreach (var permission in rolePermissions.Select(name => permissionsByName[name]))
            {
                if (!granted.Contains(permission.Id))
                {
                    dbContext.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Dictionary<string, Permission>> SeedPermissionsAsync(CancellationToken cancellationToken)
    {
        var existing = await dbContext.Permissions.ToListAsync(cancellationToken);
        var byName = existing.ToDictionary(p => p.Name, StringComparer.Ordinal);

        foreach (var name in AppPermissions.All.Where(name => !byName.ContainsKey(name)))
        {
            var (resource, action) = AppPermissions.Parse(name);
            var permission = new Permission { Resource = resource, Action = action };
            dbContext.Permissions.Add(permission);
            byName[name] = permission;
        }

        // Saved now so the new permissions have their ids before being granted to roles.
        await dbContext.SaveChangesAsync(cancellationToken);

        return byName;
    }

    private static void ThrowIfFailed(IdentityResult result, string roleName)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not seed role '{roleName}': {string.Join(", ", result.Errors.Select(e => e.Description))}");
        }
    }
}
