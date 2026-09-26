namespace Identity.API.Models;

/// <summary>
/// A security-relevant action (registration, login, logout, password reset, token reuse...).
/// Never holds a secret: no password, no raw token.
/// </summary>
public class AuditLog
{
    public long Id { get; set; }

    /// <summary>The user who performed (or was the subject of) the action, null when unknown.</summary>
    public int? ActorUserId { get; set; }

    public AppUser? ActorUser { get; set; }

    public required string Action { get; set; }

    public required string EntityType { get; set; }

    public string? EntityId { get; set; }

    public string? Details { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
