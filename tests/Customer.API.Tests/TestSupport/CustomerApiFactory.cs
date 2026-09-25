using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Customer.API.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace Customer.API.Tests.TestSupport;

/// <summary>
/// The real customer-api (Program, the whole pipeline, the real migrations run by
/// DatabaseInitializer) against a PostgreSQL container. The only connection string configured is
/// ConnectionStrings:customer-db, and every configuration key the service looks up is recorded,
/// so a test can prove it never asked for another service's database. Shared by the tests of one
/// class.
/// </summary>
public sealed class CustomerApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminRole = "Admin";
    public const string CustomerRole = "Customer";
    public const string ReadPermission = "CUSTOMER:READ";
    public const string WritePermission = "CUSTOMER:WRITE";

    private static int _nextUserId = 1000;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgreSqlFixture.Image).Build();
    private readonly KeyRecordingConfigurationSource _recorder = new();

    /// <summary>Every configuration key read by the host, in any provider, since it started.</summary>
    public IReadOnlyCollection<string> RequestedConfigurationKeys => _recorder.Keys.Keys.ToList();

    public string DatabaseConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>A user id no other test of the run has used, so profiles never collide.</summary>
    public static int NewUserId() => Interlocked.Increment(ref _nextUserId);

    /// <summary>A client carrying a Customer-role token for the given user, with the given permissions.</summary>
    public HttpClient CreateClientForUser(int userId, params string[] permissions) =>
        CreateClientWithRole(userId, CustomerRole, permissions);

    /// <summary>A client carrying a token for the given user, role and permissions.</summary>
    public HttpClient CreateClientWithRole(int userId, string role, params string[] permissions)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Create(Services, userId, role, permissions));
        return client;
    }

    /// <summary>A client for a user holding both CUSTOMER:READ and CUSTOMER:WRITE.</summary>
    public HttpClient CreateCustomerClient(int userId) => CreateClientForUser(userId, ReadPermission, WritePermission);

    /// <summary>Runs an arrange/assert step directly against the service's own database.</summary>
    public async Task<T> WithDbContextAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Host configuration is visible while Program registers its services: the Aspire
        // Npgsql integration reads the connection string at registration time. The recording
        // provider is the one holding it, so it sees the customer-db lookup, and it also sees any
        // lookup no other provider can answer (such as a connection string for another database).
        _recorder.Values["ConnectionStrings:customer-db"] = _postgres.GetConnectionString();
        builder.ConfigureHostConfiguration(configuration => configuration.Add(_recorder));

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = TestTokens.SigningKey,
            }));
    }

    /// <summary>An in-memory provider that remembers every key it was asked for.</summary>
    private sealed class KeyRecordingConfigurationSource : IConfigurationSource
    {
        public ConcurrentDictionary<string, byte> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IConfigurationProvider Build(IConfigurationBuilder builder) => new Provider(Keys, Values);

        private sealed class Provider(
            ConcurrentDictionary<string, byte> keys,
            Dictionary<string, string?> values) : ConfigurationProvider
        {
            public override void Load()
            {
                foreach (var (key, value) in values)
                {
                    Set(key, value);
                }
            }

            public override bool TryGet(string key, out string? value)
            {
                keys.TryAdd(key, 0);
                return base.TryGet(key, out value);
            }
        }
    }
}
