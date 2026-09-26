using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Order.API.Data;

/// <summary>
/// order-api's own database (order-db): orders, their idempotency keys, and MassTransit's
/// transactional outbox tables, where OrderCreatedEvent is written in the same transaction as the
/// order it describes. (MassTransit's SQL transport also lives in order-db, in its own "transport"
/// schema, created by MassTransit rather than by these EF migrations.)
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Models.Order> Orders => Set<Models.Order>();

    public DbSet<Models.IdempotencyKey> IdempotencyKeys => Set<Models.IdempotencyKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
