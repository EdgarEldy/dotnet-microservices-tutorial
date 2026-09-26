using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Order.API.Data;

/// <summary>
/// Used by the dotnet-ef tooling only (migrations add/remove/script), which runs without AppHost
/// and therefore without the injected "order-db" connection string. Adding a migration does
/// not connect to the server: the placeholder below is never used at runtime, where Aspire's
/// AddNpgsqlDbContext supplies the real connection string.
/// </summary>
public sealed class DesignTimeAppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string DesignTimeConnectionString =
        "Host=design-time-placeholder;Database=order-db;Username=postgres;Password=design-time-only";

    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(DesignTimeConnectionString)
            .Options;

        return new AppDbContext(options);
    }
}
