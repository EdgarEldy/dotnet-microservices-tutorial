using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Order.API.Data.Configurations;

public sealed class OrderConfiguration : IEntityTypeConfiguration<Models.Order>
{
    public void Configure(EntityTypeBuilder<Models.Order> builder)
    {
        builder.ToTable("orders");

        builder.HasKey(o => o.Id);

        // Plain columns referencing customer-api and catalog-api: deliberately no foreign keys.
        builder.Property(o => o.CustomerId).IsRequired();
        builder.Property(o => o.ProductId).IsRequired();

        // "Who may read this order" and "my orders" both filter on it.
        builder.HasIndex(o => o.UserId);

        builder.Property(o => o.Total).HasPrecision(18, 2);

        // Stored as text: readable in the database and stable if enum members are reordered.
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(32);
    }
}
