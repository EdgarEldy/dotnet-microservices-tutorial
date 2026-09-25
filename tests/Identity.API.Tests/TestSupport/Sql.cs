namespace Identity.API.Tests.TestSupport;

/// <summary>Row counts keyed by a test's unique e-mail (the "marker" parameter).</summary>
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

    public const string UsersWithEmail =
        """SELECT count(*) FROM "AspNetUsers" WHERE "NormalizedEmail" = upper(@marker)""";

    public const string PasswordResetAuditRowsForEmail =
        """
        SELECT count(*) FROM audit_logs a
        JOIN "AspNetUsers" u ON a.entity_id = u."Id"::text
        WHERE u."NormalizedEmail" = upper(@marker) AND a.action = 'PasswordResetRequested'
        """;
}
