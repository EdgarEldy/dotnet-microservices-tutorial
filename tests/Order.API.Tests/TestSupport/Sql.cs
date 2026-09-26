namespace Order.API.Tests.TestSupport;

/// <summary>
/// Row counts keyed by a test's "marker" parameter. Order tests use the same unique string as the
/// customer e-mail (carried by OrderCreatedEvent, so it identifies the OutboxMessage) and as the
/// Idempotency-Key (stored next to the order, so it identifies the order).
/// </summary>
public static class Sql
{
    public const string OutboxRowsForEmail =
        """SELECT count(*) FROM "OutboxMessage" WHERE "Body" LIKE '%' || @marker || '%'""";

    /// <summary>
    /// Messages still in MassTransit's PostgreSQL transport (delivered by the outbox, not yet
    /// produced to Kafka by the relay): a row is deleted once its last delivery is acknowledged.
    /// </summary>
    public const string TransportMessagesForEmail =
        """SELECT count(*) FROM transport.message WHERE body::text LIKE '%' || @marker || '%'""";

    /// <summary>Orders joined to their idempotency key: the order row and its key, together.</summary>
    public const string OrdersWithIdempotencyKey =
        """
        SELECT count(*) FROM orders o
        JOIN idempotency_keys k ON k."OrderId" = o."Id"
        WHERE k."IdempotencyKey" = @marker
        """;

    public const string IdempotencyKeys =
        """SELECT count(*) FROM idempotency_keys WHERE "IdempotencyKey" = @marker""";

    /// <summary>Orders for one customer id (the marker, as text): catches an order saved without its key.</summary>
    public const string OrdersForCustomer =
        """SELECT count(*) FROM orders WHERE "CustomerId"::text = @marker""";
}
