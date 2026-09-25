namespace Identity.API.Models;

/// <summary>
/// An access token revoked before its expiry (logout), identified by its "jti" claim.
/// The row is only meaningful until <see cref="ExpiresAt"/>: after that the token is rejected anyway.
/// </summary>
public class BlacklistedAccessToken
{
    public long Id { get; set; }

    public int UserId { get; set; }

    public AppUser User { get; set; } = null!;

    public required string Jti { get; set; }

    public DateTimeOffset BlacklistedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
