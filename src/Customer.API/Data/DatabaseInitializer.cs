using Microsoft.EntityFrameworkCore;

namespace Customer.API.Data;

/// <summary>
/// Startup step (the dotnet/eShop convention): applies pending migrations to customer-db before
/// the service serves requests; AppHost's WaitFor(customerDb) guarantees the server is up.
/// Skipped under the dotnet-ef design-time tooling.
/// </summary>
public sealed class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (EF.IsDesignTime)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        logger.LogInformation("Applying customer-db migrations");
        await dbContext.Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
