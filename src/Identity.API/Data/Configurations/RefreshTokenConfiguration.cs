using Identity.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Identity.API.Data.Configurations;

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    // Lowercase hex SHA-256: 32 bytes, 64 characters.
    public const int TokenHashLength = 64;

    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(t => t.UserId).HasColumnName("user_id");
        builder.Property(t => t.TokenHash).HasColumnName("token_hash")
            .HasMaxLength(TokenHashLength).IsFixedLength().IsRequired();
        builder.Property(t => t.FamilyId).HasColumnName("family_id");
        builder.Property(t => t.SecurityStampAtIssuance).HasColumnName("security_stamp_at_issuance")
            .HasMaxLength(256).IsRequired();
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.Property(t => t.ExpiresAt).HasColumnName("expires_at");
        builder.Property(t => t.RevokedAt).HasColumnName("revoked_at");
        builder.Property(t => t.ReplacedByTokenId).HasColumnName("replaced_by_token_id");

        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.FamilyId);
        builder.HasIndex(t => t.UserId);

        builder.HasOne(t => t.User)
            .WithMany(u => u.RefreshTokens)
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(t => t.ReplacedByToken)
            .WithOne()
            .HasForeignKey<RefreshToken>(t => t.ReplacedByTokenId)
            // NO ACTION rather than RESTRICT: checked at the end of the statement, so deleting a
            // user (which cascades to all its tokens) is not blocked by the chain between them.
            .OnDelete(DeleteBehavior.NoAction);
    }
}
