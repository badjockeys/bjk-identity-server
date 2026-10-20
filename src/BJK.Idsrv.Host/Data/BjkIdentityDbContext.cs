using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BJK.Idsrv.Host.Data;

/// <summary>
/// A sign-in account. Its <c>Id</c> is the "sub" claim in issued tokens and the id used everywhere else. The optional
/// mobile number is Identity's own <c>PhoneNumber</c> (E.164) and <c>PhoneNumberConfirmed</c> (confirmed by a texted
/// code); it's never shown to anyone else.
/// </summary>
public sealed class BjkIdentityUser : IdentityUser
{
    /// <summary>
    /// "Let people with my number find me": the mobile app may match this number, in hashed form, against contacts
    /// people choose to share. On by default for adults when the number is confirmed; always off for under-18s.
    /// </summary>
    public bool MobileDiscoverable { get; set; }

    /// <summary>When the last confirmation code was texted (for the one-a-minute limit).</summary>
    public DateTimeOffset? MobileCodeSentAt { get; set; }

    /// <summary>The UTC day <see cref="MobileCodesSent"/> counts.</summary>
    public DateOnly? MobileCodesDay { get; set; }

    /// <summary>Codes texted on <see cref="MobileCodesDay"/> (for the five-a-day limit).</summary>
    public int MobileCodesSent { get; set; }
}

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

        // One confirmed account per mobile number: finding whether another account already has it.
        builder.Entity<BjkIdentityUser>().HasIndex(u => u.PhoneNumber);
    }
}
