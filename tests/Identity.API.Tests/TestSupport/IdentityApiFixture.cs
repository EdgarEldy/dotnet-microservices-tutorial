using System.Globalization;
using Identity.API.Data;
using Identity.API.Messaging;
using Identity.API.Models;
using Identity.API.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;

namespace Identity.API.Tests.TestSupport;

/// <summary>
/// The whole identity-api (its real Program, registrations and startup: migrations, seeding,
/// MassTransit outbox, SQL transport and Kafka relay) against a real PostgreSQL 16 and a real
/// Kafka broker, both Testcontainers-managed. One instance per test class (class fixture).
/// </summary>
public sealed class IdentityApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Test-only HS256 key (>= 32 characters), supplied as Jwt:SigningKey like AppHost does.</summary>
    public const string SigningKey = "identity-api-tests-signing-key-0123456789";

    public const string ValidPassword = "Str0ng!Passw0rd";

    public static readonly TimeSpan KafkaTimeout = TimeSpan.FromSeconds(60);

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16").Build();

    private readonly KafkaContainer kafka = new KafkaBuilder("confluentinc/cp-kafka:7.5.12").Build();

    /// <summary>Registered on AppDbContext, inert until a test arms it.</summary>
    public CommitGateInterceptor CommitGate { get; } = new();

    public string DatabaseConnectionString => postgres.GetConnectionString();

    public string KafkaBootstrapServers { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), kafka.StartAsync());

        var bootstrap = new Uri(kafka.GetBootstrapAddress());
        KafkaBootstrapServers = $"{bootstrap.Host}:{bootstrap.Port.ToString(CultureInfo.InvariantCulture)}";

        await KafkaTopicReader.CreateTopicsAsync(
            KafkaBootstrapServers, KafkaTopics.UserRegistered, KafkaTopics.PasswordResetRequested);

        // Starts the host now: migrations, seeding, the SQL transport and the bus.
        _ = Services;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:identity-db", DatabaseConnectionString);
        builder.UseSetting("ConnectionStrings:kafka", KafkaBootstrapServers);
        builder.UseSetting("Jwt:SigningKey", SigningKey);

        builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(CommitGate)));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await kafka.DisposeAsync();
        await postgres.DisposeAsync();
    }

    public static string NewEmail(string prefix) =>
        $"{prefix}-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}@example.com";

    /// <summary>A confirmed account with the User role, created straight through Identity (no event).</summary>
    public async Task<AppUser> CreateConfirmedUserAsync(string email, string password = ValidPassword)
    {
        await using var scope = Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var user = new AppUser { UserName = email, Email = email, EmailConfirmed = true };
        EnsureSucceeded(await userManager.CreateAsync(user, password));
        EnsureSucceeded(await userManager.AddToRoleAsync(user, AppRoles.User));

        return user;
    }

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

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(e => e.Description)));
        }
    }
}
