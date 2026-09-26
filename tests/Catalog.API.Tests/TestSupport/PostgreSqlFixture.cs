using Catalog.API.Data;
using Catalog.API.Services;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Catalog.API.Tests.TestSupport;

/// <summary>
/// One real PostgreSQL 16 server (the image AppHost runs) per test class. Each test asks for its
/// own freshly created database, so no test ever sees another test's rows.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    public const string Image = "postgres:16";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    static PostgreSqlFixture()
    {
        // What Extensions.AddApplicationServices does at startup, needed when a service is
        // constructed directly (ProductResponse.CategoryName comes from this configuration).
        TypeAdapterConfig.GlobalSettings.Scan(typeof(ProductService).Assembly);
    }

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>Creates an empty database (no schema) and returns its connection string.</summary>
    public async Task<string> CreateEmptyDatabaseAsync(CancellationToken cancellationToken)
    {
        var databaseName = $"catalog_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = databaseName,
        }.ConnectionString;
    }

    /// <summary>Creates a database and applies the real migrations to it (no seed).</summary>
    public async Task<string> CreateMigratedDatabaseAsync(CancellationToken cancellationToken)
    {
        var connectionString = await CreateEmptyDatabaseAsync(cancellationToken);

        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.MigrateAsync(cancellationToken);

        return connectionString;
    }

    public static AppDbContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
}
