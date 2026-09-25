using Identity.API.Models;
using MassTransit;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Identity.API.Data;

/// <summary>
/// identity-api's own database (identity-db): ASP.NET Core Identity tables, permissions,
/// tokens, audit logs, MassTransit's outbox tables and the DataProtection key ring. No other
/// service ever connects to it. (MassTransit's SQL transport also lives in identity-db, in its
/// own "transport" schema, created by MassTransit rather than by these EF migrations.)
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, AppRole, int>(options), IDataProtectionKeyContext
{
    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<BlacklistedAccessToken> BlacklistedAccessTokens => Set<BlacklistedAccessToken>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>
    /// The ASP.NET Core DataProtection key ring, which signs the e-mail confirmation and password
    /// reset tokens: stored here so those tokens survive a restart and work across instances.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // The Identity mapping first (AspNetUsers, AspNetRoles, ...), then this service's own.
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // MassTransit's transactional outbox: a Publish made in a request scope is stored in
        // OutboxMessage by the same SaveChangesAsync as the business rows, then relayed after commit.
        builder.AddInboxStateEntity();
        builder.AddOutboxMessageEntity();
        builder.AddOutboxStateEntity();
    }
}
