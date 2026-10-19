using BJK.Idsrv.Host.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BJK.Idsrv.Host.Services.Security;

/// <summary>
/// Encrypts the two-step secrets ASP.NET Identity keeps in <c>auth.AspNetUserTokens</c>: the authenticator app key
/// and the recovery codes. Encrypted values are stored as <c>dp1:</c> plus a Data Protection payload (keys in SQL,
/// wrapped by Key Vault in deployed environments). Values written before this existed are plain text; they're still
/// read, and <see cref="TwoFactorSecretsBackfill"/> encrypts them at startup.
/// </summary>
public sealed class TwoFactorSecretProtector(IDataProtectionProvider provider)
{
    /// <summary>Marks an encrypted value (version 1).</summary>
    public const string Prefix = "dp1:";

    /// <summary>The login provider ASP.NET Identity's user store uses for its own tokens.</summary>
    public const string StoreLoginProvider = "[AspNetUserStore]";

    /// <summary>The token names holding secrets: the authenticator key and the recovery codes.</summary>
    public static readonly IReadOnlyList<string> SecretNames = ["AuthenticatorKey", "RecoveryCodes"];

    private readonly IDataProtector _protector = provider.CreateProtector("BJK.Idsrv.TwoFactorSecrets.v1");

    /// <summary>True for the tokens that hold two-step secrets.</summary>
    /// <param name="loginProvider">The token's login provider.</param>
    /// <param name="name">The token's name.</param>
    public static bool IsSecret(string loginProvider, string name) =>
        loginProvider == StoreLoginProvider && SecretNames.Contains(name);

    /// <summary>True if the stored value is already encrypted.</summary>
    /// <param name="stored">The stored value.</param>
    public static bool IsProtected(string? stored) => stored?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>Encrypts a secret for storage. Already encrypted values are returned unchanged.</summary>
    /// <param name="value">The plain secret.</param>
    public string Protect(string value) => IsProtected(value) ? value : Prefix + _protector.Protect(value);

    /// <summary>Decrypts a stored secret. Plain text from before encryption is returned as it is.</summary>
    /// <param name="stored">The stored value.</param>
    public string? Unprotect(string? stored) =>
        stored is not null && IsProtected(stored) ? _protector.Unprotect(stored[Prefix.Length..]) : stored;
}

/// <summary>
/// ASP.NET Identity's EF Core user store, with the authenticator key and recovery codes encrypted at rest. Identity
/// reads and writes both through <see cref="SetTokenAsync"/> and <see cref="GetTokenAsync"/>, so the rest of
/// Identity (enrolment, sign-in codes, redeeming recovery codes) works unchanged.
/// </summary>
public sealed class ProtectedUserStore(BjkIdentityDbContext context, TwoFactorSecretProtector secrets, IdentityErrorDescriber? describer = null)
    : UserStore<BjkIdentityUser, IdentityRole, BjkIdentityDbContext>(context, describer)
{
    public override Task SetTokenAsync(BjkIdentityUser user, string loginProvider, string name, string? value, CancellationToken cancellationToken) =>
        base.SetTokenAsync(
            user,
            loginProvider,
            name,
            value is not null && TwoFactorSecretProtector.IsSecret(loginProvider, name) ? secrets.Protect(value) : value,
            cancellationToken);

    public override async Task<string?> GetTokenAsync(BjkIdentityUser user, string loginProvider, string name, CancellationToken cancellationToken)
    {
        var stored = await base.GetTokenAsync(user, loginProvider, name, cancellationToken);
        return TwoFactorSecretProtector.IsSecret(loginProvider, name) ? secrets.Unprotect(stored) : stored;
    }
}

/// <summary>
/// Encrypts two-step secrets stored in plain text before encryption was added. Runs once as the app starts, after
/// migrations and after the settings checks (so a deployment without its Key Vault key stops before touching any
/// secret). Safe to run on several instances at once and more than once: each row is only updated if it still holds
/// the plain value that was read, so a secret changed in the meantime is never overwritten.
/// </summary>
internal sealed class TwoFactorSecretsBackfill(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => RunAsync(services, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BjkIdentityDbContext>();
        var secrets = scope.ServiceProvider.GetRequiredService<TwoFactorSecretProtector>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TwoFactorSecretsBackfill));

        var plain = await db.UserTokens.AsNoTracking()
            .Where(t => t.LoginProvider == TwoFactorSecretProtector.StoreLoginProvider
                && TwoFactorSecretProtector.SecretNames.Contains(t.Name)
                && t.Value != null
                && !t.Value.StartsWith(TwoFactorSecretProtector.Prefix))
            .ToListAsync(cancellationToken);

        var encrypted = 0;
        foreach (var token in plain)
        {
            var old = token.Value!;
            encrypted += await db.UserTokens
                .Where(t => t.UserId == token.UserId && t.LoginProvider == token.LoginProvider && t.Name == token.Name && t.Value == old)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.Value, secrets.Protect(old)), cancellationToken);
        }

        if (encrypted > 0)
        {
            // Counts only: never the users or the values.
            logger.LogInformation("Encrypted {Count} two-step secrets stored before encryption at rest.", encrypted);
        }
    }
}
