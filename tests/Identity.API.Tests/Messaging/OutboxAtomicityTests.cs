using System.Net;
using System.Net.Http.Json;
using Identity.API.Dtos;
using Identity.API.Messaging;
using Identity.API.Tests.TestSupport;

namespace Identity.API.Tests.Messaging;

/// <summary>
/// The README's outbox requirement, against real PostgreSQL and Kafka: UserRegisteredEvent and
/// PasswordResetRequestedEvent are written to OutboxMessage inside the same transaction as the
/// row that triggered them (visible from inside that transaction, invisible from outside), vanish
/// with it on rollback, and reach their Kafka topic only once that transaction has committed.
/// The CommitGateInterceptor stops the targeted transaction right before its commit.
/// </summary>
public sealed class OutboxAtomicityTests(IdentityApiFixture fixture) : IClassFixture<IdentityApiFixture>
{
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(30);

    // How long the topic is watched for an event that must not be there.
    private static readonly TimeSpan AbsenceWindow = TimeSpan.FromSeconds(5);

    private readonly HttpClient client = fixture.CreateClient();

    [Fact]
    public async Task Register_ShouldPersistNeitherUserNorOutboxMessage_WhenTransactionFailsAfterPublish()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = IdentityApiFixture.NewEmail("register-rollback");
        var scenario = fixture.CommitGate.Arm(email, Sql.UsersWithEmail, CommitAction.Fail);
        try
        {
            var response = await client.PostAsJsonAsync("/api/v1/Auth/Register", new RegisterRequest(email, IdentityApiFixture.ValidPassword), ct);

            // Inside the transaction, just before the (failed) commit: user and event side by side.
            var snapshot = await scenario.Reached.Task.WaitAsync(CommitTimeout, ct);
            Assert.Equal(new InTransactionSnapshot(OutboxRows: 1, BusinessRows: 1), snapshot);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            fixture.CommitGate.Disarm(scenario);
        }

        // After the rollback: neither row exists.
        Assert.Equal(0, await fixture.CountCommittedAsync(Sql.UsersWithEmail, email));
        Assert.Equal(0, await fixture.CountCommittedAsync(Sql.OutboxRowsForEmail, email));

        // A later registration goes through the whole path; the rolled back one never shows up.
        var sentinel = IdentityApiFixture.NewEmail("register-sentinel");
        var sentinelResponse = await client.PostAsJsonAsync("/api/v1/Auth/Register", new RegisterRequest(sentinel, IdentityApiFixture.ValidPassword), ct);
        Assert.Equal(HttpStatusCode.Accepted, sentinelResponse.StatusCode);

        var read = await ReadUntilEmailAsync(KafkaTopics.UserRegistered, sentinel, IdentityApiFixture.KafkaTimeout);
        Assert.NotNull(read.Match);
        Assert.DoesNotContain(read.Values, value => KafkaTopicReader.HasEmail(value, email));
    }

    [Fact]
    public async Task Register_ShouldRelayUserRegisteredEventToKafkaOnlyAfterCommit_WhenRegistrationSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = IdentityApiFixture.NewEmail("register-commit");
        var scenario = fixture.CommitGate.Arm(email, Sql.UsersWithEmail, CommitAction.Hold);
        HttpResponseMessage response;
        try
        {
            var request = client.PostAsJsonAsync("/api/v1/Auth/Register", new RegisterRequest(email, IdentityApiFixture.ValidPassword), ct);

            // Commit held: the user and its OutboxMessage exist inside the transaction...
            var snapshot = await scenario.Reached.Task.WaitAsync(CommitTimeout, ct);
            Assert.Equal(new InTransactionSnapshot(OutboxRows: 1, BusinessRows: 1), snapshot);

            // ...but not outside it, and nothing reached Kafka.
            Assert.Equal(0, await fixture.CountCommittedAsync(Sql.UsersWithEmail, email));
            Assert.Equal(0, await fixture.CountCommittedAsync(Sql.OutboxRowsForEmail, email));
            var beforeCommit = await ReadUntilEmailAsync(KafkaTopics.UserRegistered, email, AbsenceWindow);
            Assert.Null(beforeCommit.Match);

            scenario.Release.SetResult();
            response = await request.WaitAsync(CommitTimeout, ct);
        }
        finally
        {
            fixture.CommitGate.Disarm(scenario);
        }

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await fixture.CountCommittedAsync(Sql.UsersWithEmail, email));

        // After the commit, the relay delivers the event to its topic, with the committed user's id.
        var afterCommit = await ReadUntilEmailAsync(KafkaTopics.UserRegistered, email, IdentityApiFixture.KafkaTimeout);
        Assert.NotNull(afterCommit.Match);
        var userId = await fixture.QueryAsync(db => Task.FromResult(db.Users.Single(u => u.Email == email).Id));
        Assert.Equal(userId, KafkaTopicReader.GetInt32(afterCommit.Match, "userId"));
        Assert.False(string.IsNullOrEmpty(KafkaTopicReader.GetString(afterCommit.Match, "confirmationToken")));
    }

    [Fact]
    public async Task ForgotPassword_ShouldPersistNeitherAuditLogNorOutboxMessage_WhenTransactionFailsAfterPublish()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = IdentityApiFixture.NewEmail("reset-rollback");
        await fixture.CreateConfirmedUserAsync(email);
        var scenario = fixture.CommitGate.Arm(email, Sql.PasswordResetAuditRowsForEmail, CommitAction.Fail);
        try
        {
            var response = await client.PostAsJsonAsync("/api/v1/Auth/ForgotPassword", new ForgotPasswordRequest(email), ct);

            var snapshot = await scenario.Reached.Task.WaitAsync(CommitTimeout, ct);
            Assert.Equal(new InTransactionSnapshot(OutboxRows: 1, BusinessRows: 1), snapshot);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            fixture.CommitGate.Disarm(scenario);
        }

        Assert.Equal(0, await fixture.CountCommittedAsync(Sql.PasswordResetAuditRowsForEmail, email));
        Assert.Equal(0, await fixture.CountCommittedAsync(Sql.OutboxRowsForEmail, email));

        var sentinel = IdentityApiFixture.NewEmail("reset-sentinel");
        await fixture.CreateConfirmedUserAsync(sentinel);
        var sentinelResponse = await client.PostAsJsonAsync("/api/v1/Auth/ForgotPassword", new ForgotPasswordRequest(sentinel), ct);
        Assert.Equal(HttpStatusCode.Accepted, sentinelResponse.StatusCode);

        var read = await ReadUntilEmailAsync(KafkaTopics.PasswordResetRequested, sentinel, IdentityApiFixture.KafkaTimeout);
        Assert.NotNull(read.Match);
        Assert.DoesNotContain(read.Values, value => KafkaTopicReader.HasEmail(value, email));
    }

    [Fact]
    public async Task ForgotPassword_ShouldRelayPasswordResetRequestedEventToKafkaOnlyAfterCommit_WhenAccountExists()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = IdentityApiFixture.NewEmail("reset-commit");
        var user = await fixture.CreateConfirmedUserAsync(email);
        var scenario = fixture.CommitGate.Arm(email, Sql.PasswordResetAuditRowsForEmail, CommitAction.Hold);
        HttpResponseMessage response;
        try
        {
            var request = client.PostAsJsonAsync("/api/v1/Auth/ForgotPassword", new ForgotPasswordRequest(email), ct);

            var snapshot = await scenario.Reached.Task.WaitAsync(CommitTimeout, ct);
            Assert.Equal(new InTransactionSnapshot(OutboxRows: 1, BusinessRows: 1), snapshot);

            Assert.Equal(0, await fixture.CountCommittedAsync(Sql.PasswordResetAuditRowsForEmail, email));
            Assert.Equal(0, await fixture.CountCommittedAsync(Sql.OutboxRowsForEmail, email));
            var beforeCommit = await ReadUntilEmailAsync(KafkaTopics.PasswordResetRequested, email, AbsenceWindow);
            Assert.Null(beforeCommit.Match);

            scenario.Release.SetResult();
            response = await request.WaitAsync(CommitTimeout, ct);
        }
        finally
        {
            fixture.CommitGate.Disarm(scenario);
        }

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await fixture.CountCommittedAsync(Sql.PasswordResetAuditRowsForEmail, email));

        var afterCommit = await ReadUntilEmailAsync(KafkaTopics.PasswordResetRequested, email, IdentityApiFixture.KafkaTimeout);
        Assert.NotNull(afterCommit.Match);
        Assert.Equal(user.Id, KafkaTopicReader.GetInt32(afterCommit.Match, "userId"));
        Assert.False(string.IsNullOrEmpty(KafkaTopicReader.GetString(afterCommit.Match, "resetToken")));
    }

    [Fact]
    public async Task ForgotPassword_ShouldPublishNoEvent_WhenEmailIsUnknown()
    {
        var ct = TestContext.Current.CancellationToken;
        var unknown = IdentityApiFixture.NewEmail("reset-unknown");

        var response = await client.PostAsJsonAsync("/api/v1/Auth/ForgotPassword", new ForgotPasswordRequest(unknown), ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(0, await fixture.CountCommittedAsync(Sql.OutboxRowsForEmail, unknown));

        // An event requested afterwards for a real account arrives; none ever came for the unknown e-mail.
        var sentinel = IdentityApiFixture.NewEmail("reset-known");
        await fixture.CreateConfirmedUserAsync(sentinel);
        var sentinelResponse = await client.PostAsJsonAsync("/api/v1/Auth/ForgotPassword", new ForgotPasswordRequest(sentinel), ct);
        Assert.Equal(HttpStatusCode.Accepted, sentinelResponse.StatusCode);

        var read = await ReadUntilEmailAsync(KafkaTopics.PasswordResetRequested, sentinel, IdentityApiFixture.KafkaTimeout);
        Assert.NotNull(read.Match);
        Assert.DoesNotContain(read.Values, value => KafkaTopicReader.HasEmail(value, unknown));
    }

    [Fact]
    public async Task Register_ShouldAnswerBothAcceptedAndPublishOneEvent_WhenSameEmailRegistersConcurrently()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = IdentityApiFixture.NewEmail("register-race");
        var request = new RegisterRequest(email, IdentityApiFixture.ValidPassword);
        var scenario = fixture.CommitGate.Arm(email, Sql.UsersWithEmail, CommitAction.Hold);
        HttpResponseMessage first;
        HttpResponseMessage second;
        try
        {
            // First registration: user inserted, commit held, so the e-mail is still invisible to others.
            var firstRequest = client.PostAsJsonAsync("/api/v1/Auth/Register", request, ct);
            Assert.Equal(new InTransactionSnapshot(OutboxRows: 1, BusinessRows: 1), await scenario.Reached.Task.WaitAsync(CommitTimeout, ct));

            // Second registration: passes the "already registered?" lookup, then its INSERT blocks on
            // the unique index row locked by the first transaction.
            var secondRequest = client.PostAsJsonAsync("/api/v1/Auth/Register", request, ct);
            await WaitForBlockedUserInsertAsync(secondRequest, ct);

            // First commits: the blocked INSERT now fails with a unique violation (23505).
            scenario.Release.SetResult();
            first = await firstRequest.WaitAsync(CommitTimeout, ct);
            second = await secondRequest.WaitAsync(CommitTimeout, ct);
        }
        finally
        {
            fixture.CommitGate.Disarm(scenario);
        }

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(ct), await second.Content.ReadAsStringAsync(ct));
        Assert.Equal(first.Content.Headers.ContentType, second.Content.Headers.ContentType);
        Assert.Equal(1, await fixture.CountCommittedAsync(Sql.UsersWithEmail, email));

        // Exactly one UserRegisteredEvent: read the topic up to a later registration's event.
        var sentinel = IdentityApiFixture.NewEmail("register-race-sentinel");
        var sentinelResponse = await client.PostAsJsonAsync("/api/v1/Auth/Register", new RegisterRequest(sentinel, IdentityApiFixture.ValidPassword), ct);
        Assert.Equal(HttpStatusCode.Accepted, sentinelResponse.StatusCode);

        var read = await ReadUntilEmailAsync(KafkaTopics.UserRegistered, sentinel, IdentityApiFixture.KafkaTimeout);
        Assert.NotNull(read.Match);
        Assert.Single(read.Values, value => KafkaTopicReader.HasEmail(value, email));
    }

    /// <summary>
    /// Polls pg_stat_activity (bounded) until a session waits on a lock while inserting into
    /// AspNetUsers: the second registration has reached its INSERT and is blocked by the first.
    /// </summary>
    private async Task WaitForBlockedUserInsertAsync(Task<HttpResponseMessage> pendingRequest, CancellationToken ct)
    {
        const string blockedInsertSql = """
            SELECT count(*) FROM pg_stat_activity
            WHERE wait_event_type = 'Lock' AND query LIKE '%INSERT INTO "AspNetUsers"%'
            """;

        await using var connection = new Npgsql.NpgsqlConnection(fixture.DatabaseConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new Npgsql.NpgsqlCommand(blockedInsertSql, connection);

        var deadline = TimeProvider.System.GetUtcNow() + CommitTimeout;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            if (pendingRequest.IsCompleted)
            {
                var response = await pendingRequest;
                Assert.Fail($"The second registration completed ({(int)response.StatusCode}) before reaching the blocked INSERT.");
            }

            if ((long)(await command.ExecuteScalarAsync(ct))! > 0)
            {
                return;
            }

            await Task.Yield();
        }

        Assert.Fail("The second registration never blocked on the AspNetUsers unique index.");
    }

    private Task<KafkaReadResult> ReadUntilEmailAsync(string topic, string email, TimeSpan timeout) =>
        KafkaTopicReader.ReadUntilAsync(
            fixture.KafkaBootstrapServers, topic, value => KafkaTopicReader.HasEmail(value, email), timeout);
}
