using Identity.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Identity.API.Data.Configurations;

public sealed class BlacklistedAccessTokenConfiguration : IEntityTypeConfiguration<BlacklistedAccessToken>
{
    public void Configure(EntityTypeBuilder<BlacklistedAccessToken> builder)
    {
        builder.ToTable("blacklisted_access_tokens");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.UserId).HasColumnName("user_id");
        builder.Property(t => t.Jti).HasColumnName("jti").HasMaxLength(64).IsRequired();
        builder.Property(t => t.BlacklistedAt).HasColumnName("blacklisted_at");
        builder.Property(t => t.ExpiresAt).HasColumnName("expires_at");

        // Looked up on every authenticated request.
        builder.HasIndex(t => t.Jti).IsUnique();
        builder.HasIndex(t => t.ExpiresAt);

        builder.HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
