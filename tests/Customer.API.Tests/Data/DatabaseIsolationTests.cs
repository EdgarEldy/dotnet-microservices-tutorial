using System.Net;
using Customer.API.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Customer.API.Tests.Data;

/// <summary>
/// README: customer-api never attempts a direct database call against identity-api's schema.
/// Proven three ways: the EF model knows one table and no foreign key, the running service only
/// ever looks up the customer-db connection string, and the database it talks to is customer-db.
/// </summary>
public sealed class DatabaseIsolationTests(CustomerApiFactory factory) : IClassFixture<CustomerApiFactory>
{
    [Fact]
    public async Task Model_ShouldMapOnlyCustomersTableWithoutForeignKey_WhenServiceIsRunning()
    {
        var (tables, foreignKeys, userIdColumn) = await factory.WithDbContextAsync(db =>
        {
            var entityTypes = db.Model.GetEntityTypes().ToList();
            var customer = Assert.Single(entityTypes);
            return Task.FromResult((
                entityTypes.Select(e => e.GetTableName()).ToList(),
                entityTypes.SelectMany(e => e.GetForeignKeys()).Count(),
                customer.FindProperty(nameof(Models.Customer.UserId))!.GetColumnName()));
        });

        Assert.Equal(["customers"], tables);
        Assert.Equal(0, foreignKeys);
        Assert.Equal("UserId", userIdColumn);
    }

    [Fact]
    public async Task Configuration_ShouldOnlyReadCustomerDbConnectionString_WhenServingRequests()
    {
        using var client = factory.CreateCustomerClient(CustomerApiFactory.NewUserId());

        // A request that goes all the way to the database (404 = the query ran and found nothing).
        using var response = await client.GetAsync("/api/v1/Customers/999999", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var connectionStringKeys = factory.RequestedConfigurationKeys
            .Where(k => k.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.ToLowerInvariant())
            .Distinct()
            .ToList();
        Assert.Equal(["connectionstrings:customer-db"], connectionStringKeys);
        Assert.DoesNotContain(factory.RequestedConfigurationKeys, k => k.Contains("identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Database_ShouldBeConfiguredCustomerDbWithOnlyCustomerTables_WhenServiceHasStarted()
    {
        var expectedDatabase = new NpgsqlConnectionStringBuilder(factory.DatabaseConnectionString).Database;

        var (database, tables, foreignKeyCount) = await factory.WithDbContextAsync(async db =>
        {
            var name = db.Database.GetDbConnection().Database;
            var tableNames = await db.Database
                .SqlQueryRaw<string>(
                    "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema')")
                .ToListAsync(TestContext.Current.CancellationToken);
            var foreignKeys = await db.Database
                .SqlQueryRaw<int>(
                    "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.table_constraints WHERE constraint_type = 'FOREIGN KEY'")
                .SingleAsync(TestContext.Current.CancellationToken);
            return (name, tableNames, foreignKeys);
        });

        Assert.Equal(expectedDatabase, database);
        Assert.Equal(["__EFMigrationsHistory", "customers"], tables.Order(StringComparer.Ordinal));
        Assert.Equal(0, foreignKeyCount);
    }
}
