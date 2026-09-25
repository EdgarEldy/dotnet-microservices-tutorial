using Catalog.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.API.Data.Configurations;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.ProductName).HasMaxLength(CatalogLimits.ProductNameMaxLength).IsRequired();
        builder.Property(p => p.UnitPrice)
            .HasPrecision(CatalogLimits.UnitPricePrecision, CatalogLimits.UnitPriceScale);

        // Restrict: a category cannot be deleted while it still has products.
        builder.HasOne(p => p.Category)
            .WithMany(c => c.Products)
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
