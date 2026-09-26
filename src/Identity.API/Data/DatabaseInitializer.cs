using Identity.API.Services;
using Microsoft.EntityFrameworkCore;

namespace Identity.API.Data;

/// <summary>
/// Startup step (the dotnet/eShop convention): applies pending migrations to identity-db, then
/// seeds roles and permissions through <see cref="IRoleService"/>. Runs from StartAsync, so the
/// service does not serve requests before its schema exists; AppHost's WaitFor(identityDb)
/// guarantees the server is up. Skipped under the dotnet-ef design-time tooling.
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
        logger.LogInformation("Applying identity-db migrations");
        await dbContext.Database.MigrateAsync(cancellationToken);

        await scope.ServiceProvider.GetRequiredService<IRoleService>().SeedAsync(cancellationToken);
        logger.LogInformation("identity-db is up to date");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
