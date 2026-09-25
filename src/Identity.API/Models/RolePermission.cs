namespace Identity.API.Models;

/// <summary>
/// Join entity between <see cref="AppRole"/> and <see cref="Permission"/> (role_permissions).
/// </summary>
public class RolePermission
{
    public int RoleId { get; set; }

    public AppRole Role { get; set; } = null!;

    public int PermissionId { get; set; }

    public Permission Permission { get; set; } = null!;
}
