using BJK.Idsrv.Host.Data;
using BJK.Modules.Shared.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BJK.Idsrv.Host.Services;

/// <summary>Section <c>Admin</c>.</summary>
public sealed class AdminBootstrapOptions
{
    public const string SectionName = "Admin";

    /// <summary>
    /// Emails of accounts to make Admins at startup, so the first admin can exist before there's anyone to appoint
    /// them. Only applies to accounts that exist and have confirmed their email. After that, admins manage roles in
    /// the back-office, and this list can be emptied.
    /// </summary>
    public string[] BootstrapEmails { get; set; } = [];
}

internal static class IdentitySeeder
{
    /// <summary>Creates the staff roles if they're missing, then applies <see cref="AdminBootstrapOptions"/>.</summary>
    public static async Task SeedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<BjkIdentityUser>>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<AdminBootstrapOptions>>().Value;
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(IdentitySeeder));

        foreach (var role in StaffRoles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new IdentityRole(role));
                logger.LogInformation("Created the {Role} role.", role);
            }
        }

        foreach (var email in options.BootstrapEmails.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            var user = await users.FindByEmailAsync(email.Trim());
            if (user is null || !user.EmailConfirmed)
            {
                logger.LogWarning("Admin bootstrap: {Email} has no account with a confirmed email yet.", email);
                continue;
            }

            if (!await users.IsInRoleAsync(user, StaffRoles.Admin))
            {
                await users.AddToRoleAsync(user, StaffRoles.Admin);
                logger.LogInformation("Admin bootstrap: account {UserId} is now an Admin.", user.Id);
            }
        }
    }
}
