namespace Identity.API.Models;

/// <summary>
/// One refresh token of a rotation family. Only the SHA-256 hash of the raw token is stored.
/// Every token issued by rotation keeps the <see cref="FamilyId"/> of the login that started
/// the chain, so presenting an already rotated (revoked) token revokes the whole family.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }

    public int UserId { get; set; }

    public AppUser User { get; set; } = null!;

    /// <summary>Lowercase hex SHA-256 of the raw token. The raw token is never stored nor logged.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Identifies the login session; also carried as the "sid" claim of its access tokens.</summary>
    public Guid FamilyId { get; set; }

    /// <summary>The user's SecurityStamp when the token was issued: a mismatch revokes the family.</summary>
    public required string SecurityStampAtIssuance { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>The token this one was rotated into, null while it is the family's current token.</summary>
    public Guid? ReplacedByTokenId { get; set; }

    public RefreshToken? ReplacedByToken { get; set; }
}
