using Microsoft.EntityFrameworkCore;

namespace Order.API.Data;

/// <summary>
/// Startup step (the dotnet/eShop convention): applies pending migrations to order-db before
/// the service serves requests; AppHost's WaitFor(orderDb) guarantees the server is up.
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
        logger.LogInformation("Applying order-db migrations");
        await dbContext.Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
