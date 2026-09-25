using System.Net.Http.Headers;
using Catalog.API.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace Catalog.API.Tests.TestSupport;

/// <summary>
/// The real catalog-api (Program, the whole pipeline, the real migrations run by
/// DatabaseInitializer) against a PostgreSQL container, with the sample seed turned off so the
/// catalog starts empty. Shared by the tests of one class.
/// </summary>
public sealed class CatalogApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminRole = "Admin";
    public const string CustomerRole = "Customer";
    public const string WritePermission = "CATALOG:WRITE";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgreSqlFixture.Image).Build();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>A client carrying a token with the given role and permissions.</summary>
    public HttpClient CreateClientWithToken(string role, params string[] permissions)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Create(Services, role, permissions));
        return client;
    }

    public HttpClient CreateAdminClient() => CreateClientWithToken(AdminRole, WritePermission);

    public HttpClient CreateCustomerClient() => CreateClientWithToken(CustomerRole);

    /// <summary>Runs an arrange/assert step directly against the service's own database.</summary>
    public async Task<T> WithDbContextAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Host configuration is visible while Program registers its services: the Aspire
        // Npgsql integration reads the connection string at registration time.
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:catalog-db"] = _postgres.GetConnectionString(),
            }));

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Added last, so these win over appsettings.json (Catalog:SeedSampleData is true there).
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = TestTokens.SigningKey,
                [DatabaseInitializer.SeedSampleDataKey] = "false",
            }));
    }
}
