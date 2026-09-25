using Microsoft.EntityFrameworkCore;

namespace Catalog.API.Data;

/// <summary>
/// Startup step (the dotnet/eShop convention): applies pending migrations to catalog-db, then
/// seeds the sample catalog unless "Catalog:SeedSampleData" is false (tests that need an empty
/// catalog turn it off). Runs from StartAsync, so the service does not serve requests before
/// its schema exists; AppHost's WaitFor(catalogDb) guarantees the server is up. Skipped under
/// the dotnet-ef design-time tooling.
/// </summary>
public sealed class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    public const string SeedSampleDataKey = "Catalog:SeedSampleData";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (EF.IsDesignTime)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        logger.LogInformation("Applying catalog-db migrations");
        await dbContext.Database.MigrateAsync(cancellationToken);

        if (configuration.GetValue(SeedSampleDataKey, defaultValue: true))
        {
            await CatalogSeeder.SeedAsync(dbContext, cancellationToken);
        }

        logger.LogInformation("catalog-db is up to date");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
