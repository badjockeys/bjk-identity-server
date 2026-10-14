using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BJK.Idsrv.Host.Data;

/// <summary>A sign-in account. Its <c>Id</c> is the "sub" claim in issued tokens and the User module's IdentityId.</summary>
public sealed class BjkIdentityUser : IdentityUser;

/// <summary>ASP.NET Core Identity tables (accounts, password hashes, lockout), in the <see cref="Schema"/> schema.</summary>
public sealed class BjkIdentityDbContext(DbContextOptions<BjkIdentityDbContext> options)
    : IdentityDbContext<BjkIdentityUser>(options)
{
    /// <summary>"identity" is a reserved word in SQL Server, hence "auth".</summary>
    public const string Schema = "auth";

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);
    }
}
