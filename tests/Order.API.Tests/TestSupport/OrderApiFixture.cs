using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Npgsql;
using Order.API.Data;
using Order.API.Messaging;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Order.API.Tests.TestSupport;

/// <summary>
/// The whole order-api (its real Program, registrations and startup: migrations, MassTransit
/// outbox, SQL transport, Kafka relay and the two Saga consumers) against a real PostgreSQL 16 and
/// a real Kafka broker, both Testcontainers-managed. catalog-api and customer-api are two WireMock
/// servers, reached through service discovery exactly as AppHost wires them. One instance for the
/// whole assembly (collection fixture): every test uses its own ids, keys and e-mails.
/// </summary>
public sealed class OrderApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ReadPermission = "ORDER:READ";
    public const string WritePermission = "ORDER:WRITE";
    public const string UserRole = "User";
    public const string AdminRole = "Admin";

    public static readonly TimeSpan KafkaTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The standard resilience pipeline stays in place (retries, attempt timeout), only faster: a
    /// retried 5xx costs milliseconds instead of the default exponential 2 s backoff, and a hung
    /// downstream call gives up after 2 s per attempt instead of 10 s.
    /// </summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(2);

    private static int nextId = 10_000;

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16").Build();

    private readonly KafkaContainer kafka = new KafkaBuilder("confluentinc/cp-kafka:7.5.12").Build();

    /// <summary>Stands in for catalog-api (GET /api/v1/Catalog/Products/{id}).</summary>
    public WireMockServer Catalog { get; } = WireMockServer.Start();

    /// <summary>Stands in for customer-api (GET /api/v1/Customers/{id}).</summary>
    public WireMockServer Customers { get; } = WireMockServer.Start();

    /// <summary>Registered on AppDbContext, inert until a test arms it.</summary>
    public CommitGateInterceptor CommitGate { get; } = new();

    public string DatabaseConnectionString => postgres.GetConnectionString();

    public string KafkaBootstrapServers { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), kafka.StartAsync());

        var bootstrap = new Uri(kafka.GetBootstrapAddress());
        KafkaBootstrapServers = $"{bootstrap.Host}:{bootstrap.Port.ToString(CultureInfo.InvariantCulture)}";

        // One partition each, created before the host starts, so the consumers read them in order.
        await KafkaTopicReader.CreateTopicsAsync(
            KafkaBootstrapServers, KafkaTopics.OrderCreated, KafkaTopics.OrderConfirmed, KafkaTopics.NotificationFailed);

        // Starts the host now: migrations, the SQL transport, the bus and the Kafka rider.
        _ = Services;

        // WireMock's first request is slow (its own JIT and matchers): pay it here, not inside a
        // test's attempt timeout.
        using var warmUp = new HttpClient();
        foreach (var url in new[] { Catalog.Url!, Customers.Url! })
        {
            using var response = await warmUp.GetAsync(new Uri($"{url}/__warm-up"));
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:order-db", DatabaseConnectionString);
        builder.UseSetting("ConnectionStrings:kafka", KafkaBootstrapServers);
        builder.UseSetting("Jwt:SigningKey", TestTokens.SigningKey);

        // What AppHost's WithReference(catalogService) / WithReference(customerService) inject.
        builder.UseSetting("services:catalog-api:http:0", Catalog.Url);
        builder.UseSetting("services:customer-api:http:0", Customers.Url);

        builder.ConfigureTestServices(services =>
        {
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(CommitGate));
            services.ConfigureAll<HttpStandardResilienceOptions>(options =>
            {
                options.Retry.Delay = TimeSpan.FromMilliseconds(10);
                options.AttemptTimeout.Timeout = AttemptTimeout;
            });
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Catalog.Stop();
        Customers.Stop();
        await kafka.DisposeAsync();
        await postgres.DisposeAsync();
    }

    /// <summary>An id (user, product, customer) no other test of the run has used.</summary>
    public static int NewId() => Interlocked.Increment(ref nextId);

    /// <summary>A unique string, used as both the customer e-mail and the Idempotency-Key of one test.</summary>
    public static string NewMarker(string prefix) =>
        $"{prefix}-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}@example.com";

    public string CreateToken(int userId, string role, params string[] permissions) =>
        TestTokens.Create(Services, userId, role, permissions);

    /// <summary>A client for a User-role caller holding ORDER:READ and ORDER:WRITE.</summary>
    public HttpClient CreateClientForUser(int userId) =>
        CreateClientWithToken(CreateToken(userId, UserRole, ReadPermission, WritePermission));

    public HttpClient CreateAdminClient(int userId) =>
        CreateClientWithToken(CreateToken(userId, AdminRole, ReadPermission, WritePermission));

    public HttpClient CreateClientWithToken(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static string ProductPath(int productId) => $"/api/v1/Catalog/Products/{productId}";

    public static string CustomerPath(int customerId) => $"/api/v1/Customers/{customerId}";

    /// <summary>catalog-api answers the product; the mapping id lets a test replace it later.</summary>
    public void StubProduct(int productId, decimal unitPrice, string productName, Guid? mappingId = null) =>
        Catalog.Given(Request.Create().WithPath(ProductPath(productId)).UsingGet())
            .WithGuid(mappingId ?? Guid.NewGuid())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = productId,
                categoryId = 1,
                categoryName = "Books",
                productName,
                unitPrice,
            }));

    public void StubCustomer(int customerId, int userId, string email) =>
        Customers.Given(Request.Create().WithPath(CustomerPath(customerId)).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = customerId,
                userId,
                firstName = "Ada",
                lastName = "Lovelace",
                telephone = "+33600000000",
                email,
                address = "1 Test Street",
            }));

    public static void StubStatus(WireMockServer server, string path, int statusCode, Guid? mappingId = null) =>
        server.Given(Request.Create().WithPath(path).UsingGet())
            .WithGuid(mappingId ?? Guid.NewGuid())
            .RespondWith(Response.Create().WithStatusCode(statusCode));

    /// <summary>Runs <paramref name="query"/> on a fresh AppDbContext scope (committed data only).</summary>
    public async Task<T> QueryAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        dbContext.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        return await query(dbContext);
    }

    /// <summary>Counts rows on a separate connection, so only committed rows are visible.</summary>
    public async Task<int> CountCommittedAsync(string sql, string marker)
    {
        await using var connection = new NpgsqlConnection(DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("marker", marker);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Waits (bounded) until no OutboxMessage and no SQL transport message mentions
    /// <paramref name="email"/>. The relay acknowledges a transport message only after Kafka has
    /// acknowledged its produce, so from then on every committed event for that e-mail is on its
    /// topic, whatever its partition and whatever the relay's ordering.
    /// </summary>
    public async Task WaitUntilRelayedAsync(string email)
    {
        var deadline = TimeProvider.System.GetUtcNow() + KafkaTimeout;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            if (await CountCommittedAsync(Sql.OutboxRowsForEmail, email) == 0
                && await CountCommittedAsync(Sql.TransportMessagesForEmail, email) == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Fail($"Events for {email} were still waiting in the outbox or the SQL transport after {KafkaTimeout}.");
    }

    /// <summary>OrderCreatedEvents for <paramref name="email"/> among everything produced so far.</summary>
    public async Task<int> CountOrderCreatedEventsAsync(string email) =>
        (await KafkaTopicReader.ReadToEndAsync(KafkaBootstrapServers, KafkaTopics.OrderCreated, KafkaTimeout))
            .Count(value => KafkaTopicReader.HasEmail(value, email));

    /// <summary>Waits (bounded) for the OrderCreatedEvent carrying <paramref name="email"/>.</summary>
    public Task<KafkaReadResult> ReadOrderCreatedEventAsync(string email) =>
        KafkaTopicReader.ReadUntilAsync(
            KafkaBootstrapServers, KafkaTopics.OrderCreated, value => KafkaTopicReader.HasEmail(value, email), KafkaTimeout);

    /// <summary>
    /// Produces <paramref name="message"/> as the raw camelCase JSON body that order-api's Kafka
    /// topic endpoints deserialize by default (a MassTransit envelope would be read as the message
    /// itself and yield OrderId 0), with its content type in the Kafka headers.
    /// </summary>
    public async Task ProduceAsync<T>(string topic, string key, T message)
        where T : class
    {
        var body = JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = KafkaBootstrapServers }).Build();
        await producer.ProduceAsync(topic, new Message<string, string>
        {
            Key = key,
            Value = body,
            Headers = new Headers { { "Content-Type", Encoding.UTF8.GetBytes("application/json") } },
        });
    }
}

[CollectionDefinition(Name)]
public sealed class OrderApiCollection : ICollectionFixture<OrderApiFixture>
{
    public const string Name = "order-api";
}
