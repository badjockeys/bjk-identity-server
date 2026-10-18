using BJK.Idsrv.Host.Data;
using BJK.Modules.User.Infrastructure.Services;
using Duende.IdentityServer.Stores;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BJK.Idsrv.Host.Services;

/// <summary>The User module's view of sign-in accounts, backed by ASP.NET Core Identity.</summary>
internal sealed class AspNetIdentityService(
    UserManager<BjkIdentityUser> userManager,
    IPersistedGrantStore grants,
    IPasswordResetSender resetSender,
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
        return user is null ? null : ToSignInAccount(user);
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

        return users.ToDictionary(u => u.Id, ToSignInAccount, StringComparer.OrdinalIgnoreCase);
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

    private static SignInAccount ToSignInAccount(BjkIdentityUser user) =>
        new(user.Id, user.Email ?? string.Empty, user.EmailConfirmed);

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
