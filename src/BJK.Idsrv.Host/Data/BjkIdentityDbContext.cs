using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BJK.Idsrv.Host.Data;

/// <summary>A sign-in account. Its <c>Id</c> is the "sub" claim in issued tokens and the id used everywhere else.</summary>
public sealed class BjkIdentityUser : IdentityUser;

/// <summary>
/// ASP.NET Core Identity tables (accounts, password hashes, lockout, roles, external logins, two-factor settings) and
/// the Data Protection keys, in the <see cref="Schema"/> schema.
/// </summary>
public sealed class BjkIdentityDbContext(DbContextOptions<BjkIdentityDbContext> options)
    : IdentityDbContext<BjkIdentityUser>(options), IDataProtectionKeyContext
{
    /// <summary>"identity" is a reserved word in SQL Server, hence "auth".</summary>
    public const string Schema = "auth";

    /// <summary>
    /// Keys that protect sign-in cookies, antiforgery tokens and two-factor tokens. Kept in the database so every
    /// instance of the identity server can read what another wrote, and they survive redeploys.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);
    }
}
