using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Order.API.Data.Configurations;

public sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<Models.IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<Models.IdempotencyKey> builder)
    {
        builder.ToTable("idempotency_keys");

        builder.HasKey(k => k.Id);

        // UNIQUE: two concurrent requests with the same key cannot both create an order.
        builder.Property(k => k.Key).HasColumnName("IdempotencyKey").HasMaxLength(OrderLimits.IdempotencyKeyMaxLength).IsRequired();
        builder.HasIndex(k => k.Key).IsUnique();

        // A real foreign key: both tables live in order-db.
        builder.HasOne(k => k.Order).WithMany().HasForeignKey(k => k.OrderId).OnDelete(DeleteBehavior.Cascade);
    }
}
