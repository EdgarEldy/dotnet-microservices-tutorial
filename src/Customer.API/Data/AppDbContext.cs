using Microsoft.EntityFrameworkCore;

namespace Customer.API.Data;

/// <summary>
/// customer-api's own database (customer-db). No other service ever connects to it; order-api
/// reads a customer through GET /api/v1/Customers/{id}.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Models.Customer> Customers => Set<Models.Customer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
