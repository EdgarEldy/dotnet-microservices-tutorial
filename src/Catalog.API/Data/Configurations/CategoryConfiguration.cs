using Catalog.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.API.Data.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.CategoryName).HasMaxLength(CatalogLimits.CategoryNameMaxLength).IsRequired();

        // Backs the "duplicate category name" business rule, including under concurrent inserts.
        builder.HasIndex(c => c.CategoryName).IsUnique();
    }
}
