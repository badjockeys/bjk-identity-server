using System.Text;
using BJK.Idsrv.Host.Data;
using BJK.Modules.Shared.Security;
using BJK.Modules.User.Infrastructure.Services;
using Duende.IdentityServer.Stores;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace BJK.Idsrv.Host.Services;

/// <summary>The User module's view of sign-in accounts, backed by ASP.NET Core Identity.</summary>
internal sealed class AspNetIdentityService(
    UserManager<BjkIdentityUser> userManager,
    BjkIdentityDbContext db,
    IPersistedGrantStore grants,
    IPasswordResetSender resetSender,
    IEmailConfirmationSender confirmationSender,
    TimeProvider clock,
    ILogger<AspNetIdentityService> logger)
    : IIdentityService
{
    public async Task<AccountResult> CreateAccountAsync(string email, string password, CancellationToken cancellationToken)
    {
        var user = new BjkIdentityUser { UserName = email, Email = email };
        var result = await userManager.CreateAsync(user, password);

        return result.Succeeded
            ? AccountResult.Success(user.Id)
            : AccountResult.Failed(MapErrors(result, passwordField: "Password"));
    }

    public async Task<SignInAccount?> GetAccountAsync(string identityId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(identityId);
        return user is null ? null : ToSignInAccount(user, await RolesOfAsync([user.Id], cancellationToken));
    }

    public async Task<IReadOnlyDictionary<string, SignInAccount>> GetAccountsAsync(
        IReadOnlyCollection<string> identityIds,
        CancellationToken cancellationToken)
    {
        if (identityIds.Count == 0)
        {
            return new Dictionary<string, SignInAccount>();
        }

        var users = await userManager.Users
            .Where(u => identityIds.Contains(u.Id))
            .ToListAsync(cancellationToken);

        var roles = await RolesOfAsync(users.Select(u => u.Id).ToList(), cancellationToken);
        return users.ToDictionary(u => u.Id, u => ToSignInAccount(u, roles), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> CheckPasswordAsync(string identityId, string password, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(identityId);
        return user is not null && await userManager.CheckPasswordAsync(user, password);
    }

    public async Task<string?> FindByCredentialsAsync(string email, string password, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(email);

        // CheckPasswordAsync ignores lockout, which is how deactivated accounts are kept from signing in.
        return user is not null && await userManager.CheckPasswordAsync(user, password) ? user.Id : null;
    }

    public async Task DisableAccountAsync(string identityId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(identityId);
        if (user is null)
        {
            return;
        }

        await userManager.SetLockoutEnabledAsync(user, true);
        await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);

        // Ends sign-in sessions and revokes refresh tokens, so the user is signed out everywhere.
        await userManager.UpdateSecurityStampAsync(user);
        await grants.RemoveAllAsync(new PersistedGrantFilter { SubjectId = user.Id }, cancellationToken);
    }

    public async Task EnableAccountAsync(string identityId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(identityId);
        if (user is null)
        {
            return;
        }

        await userManager.SetLockoutEndDateAsync(user, null);
        await userManager.ResetAccessFailedCountAsync(user);
    }

    public async Task DeleteAccountAsync(string identityId, CancellationToken cancellationToken)
    {
        await grants.RemoveAllAsync(new PersistedGrantFilter { SubjectId = identityId }, cancellationToken);

        var user = await userManager.FindByIdAsync(identityId);
        if (user is not null)
        {
            await userManager.DeleteAsync(user);
        }
    }

    public async Task SendEmailConfirmationAsync(string identityId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(identityId);
        if (user is null || user.EmailConfirmed || string.IsNullOrEmpty(user.Email))
        {
            return;
        }

        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        try
        {
            await confirmationSender.SendAsync(user.Email, user.Id, code, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Registration has already succeeded; the user can ask for another email.
            logger.LogError(ex, "Couldn't send the email confirmation to account {UserId}.", user.Id);
        }
    }

    public async Task<AccountResult> ConfirmEmailAsync(string identityId, string token, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(identityId);
        if (user is null)
        {
            return AccountResult.Failed([new AccountError("Token", "The confirmation link is invalid or has expired.")]);
        }

        if (user.EmailConfirmed)
        {
            return AccountResult.Success(user.Id);
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
        }
        catch (FormatException)
        {
            return AccountResult.Failed([new AccountError("Token", "The confirmation link is invalid or has expired.")]);
        }

        var result = await userManager.ConfirmEmailAsync(user, decoded);
        return result.Succeeded
            ? AccountResult.Success(user.Id)
            : AccountResult.Failed([new AccountError("Token", "The confirmation link is invalid or has expired.")]);
    }

    public async Task SetRolesAsync(string identityId, IReadOnlyCollection<string> roles, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(identityId)
            ?? throw new InvalidOperationException($"Account {identityId} doesn't exist.");

        var current = await userManager.GetRolesAsync(user);
        var remove = current.Where(r => StaffRoles.All.Contains(r) && !roles.Contains(r)).ToList();
        var add = roles.Where(r => !current.Contains(r)).ToList();

        if (remove.Count > 0)
        {
            ThrowIfFailed(await userManager.RemoveFromRolesAsync(user, remove));
        }

        if (add.Count > 0)
        {
            ThrowIfFailed(await userManager.AddToRolesAsync(user, add));
        }

        // New tokens carry the new roles; ending sessions makes the change take effect straight away.
        await userManager.UpdateSecurityStampAsync(user);
        await grants.RemoveAllAsync(new PersistedGrantFilter { SubjectId = user.Id }, cancellationToken);
    }

    public async Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null || IsDisabled(user))
        {
            // Deliberately silent to the caller, so it can't be used to check which emails are registered.
            logger.LogInformation("Password reset requested for an unknown or deactivated account.");
            return;
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        await resetSender.SendAsync(email, token, cancellationToken);
    }

    public async Task<AccountResult> ResetPasswordAsync(
        string email,
        string token,
        string newPassword,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null || IsDisabled(user))
        {
            return AccountResult.Failed([new AccountError("Token", "The reset token is invalid or has expired.")]);
        }

        var result = await userManager.ResetPasswordAsync(user, token, newPassword);
        if (result.Succeeded)
        {
            // A successful reset also clears any lockout from failed sign-in attempts.
            await userManager.SetLockoutEndDateAsync(user, null);
            await userManager.ResetAccessFailedCountAsync(user);
            return AccountResult.Success(user.Id);
        }

        return AccountResult.Failed(MapErrors(result, passwordField: "NewPassword"));
    }

    private static SignInAccount ToSignInAccount(BjkIdentityUser user, IReadOnlyDictionary<string, List<string>> roles) =>
        new(
            user.Id,
            user.Email ?? string.Empty,
            user.EmailConfirmed,
            user.TwoFactorEnabled,
            roles.TryGetValue(user.Id, out var userRoles) ? userRoles : []);

    private async Task<IReadOnlyDictionary<string, List<string>>> RolesOfAsync(
        IReadOnlyCollection<string> userIds,
        CancellationToken cancellationToken)
    {
        var rows = await (
                from ur in db.UserRoles
                join role in db.Roles on ur.RoleId equals role.Id
                where userIds.Contains(ur.UserId)
                select new { ur.UserId, role.Name })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Name!).Order(StringComparer.Ordinal).ToList());
    }

    private static void ThrowIfFailed(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }

    // Deactivated accounts are locked out "forever"; ordinary lockouts after failed sign-ins last minutes.
    private bool IsDisabled(BjkIdentityUser user) =>
        user.LockoutEnd is { } end && end > clock.GetUtcNow().AddYears(100);

    private static IEnumerable<AccountError> MapErrors(IdentityResult result, string passwordField) =>
        result.Errors.Select(e => new AccountError(FieldFor(e.Code, passwordField), e.Description));

    private static string FieldFor(string code, string passwordField) => code switch
    {
        _ when code.StartsWith("Password", StringComparison.Ordinal) => passwordField,
        "DuplicateEmail" or "InvalidEmail" or "DuplicateUserName" or "InvalidUserName" => "Email",
        "InvalidToken" => "Token",
        _ => string.Empty,
    };
}
