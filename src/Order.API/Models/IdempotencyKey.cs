namespace Order.API.Models;

/// <summary>
/// The client-supplied Idempotency-Key of a POST /api/v1/Orders, stored with the order it
/// created, in the same transaction. A retried request carrying the same key gets that order
/// back instead of creating a second one.
/// </summary>
public class IdempotencyKey
{
    public int Id { get; set; }

    public required string Key { get; set; }

    public int OrderId { get; set; }

    public Order Order { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
}
