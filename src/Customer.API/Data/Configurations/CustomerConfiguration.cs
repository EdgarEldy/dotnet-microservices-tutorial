using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Customer.API.Data.Configurations;

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Models.Customer>
{
    public void Configure(EntityTypeBuilder<Models.Customer> builder)
    {
        builder.ToTable("customers");

        builder.HasKey(c => c.Id);

        // A plain column referencing identity-api's AspNetUsers.Id: deliberately no foreign key,
        // the users table lives in another service's database. Unique: one profile per user.
        builder.Property(c => c.UserId).IsRequired();
        builder.HasIndex(c => c.UserId).IsUnique();

        builder.Property(c => c.FirstName).HasMaxLength(CustomerLimits.NameMaxLength).IsRequired();
        builder.Property(c => c.LastName).HasMaxLength(CustomerLimits.NameMaxLength).IsRequired();
        builder.Property(c => c.Telephone).HasMaxLength(CustomerLimits.TelephoneMaxLength).IsRequired();
        builder.Property(c => c.Email).HasMaxLength(CustomerLimits.EmailMaxLength).IsRequired();
        builder.Property(c => c.Address).HasMaxLength(CustomerLimits.AddressMaxLength).IsRequired();
    }
}
