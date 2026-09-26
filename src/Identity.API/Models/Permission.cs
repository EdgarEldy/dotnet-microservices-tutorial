namespace Identity.API.Models;

/// <summary>
/// A fine-grained right, RESOURCE:ACTION (for example ORDER:WRITE), beyond Identity's role-only model.
/// Emitted as one "permission" claim per distinct value in the access token.
/// </summary>
public class Permission
{
    public int Id { get; set; }

    public required string Resource { get; set; }

    public required string Action { get; set; }

    public ICollection<RolePermission> RolePermissions { get; set; } = [];

    /// <summary>The claim value, RESOURCE:ACTION.</summary>
    public string Name => $"{Resource}:{Action}";
}
