using BJK.Idsrv.Host.Data;
using BJK.Modules.User.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;

namespace BJK.Idsrv.Host.Services;

/// <summary>The User module's view of sign-in accounts, backed by ASP.NET Core Identity.</summary>
internal sealed class AspNetIdentityService(
    UserManager<BjkIdentityUser> userManager,
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

    public async Task DeleteAccountAsync(string identityId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(identityId);
        if (user is not null)
        {
            await userManager.DeleteAsync(user);
        }
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

        // Invalidates existing sign-in sessions.
        await userManager.UpdateSecurityStampAsync(user);
    }

    public async Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null || IsDisabled(user))
        {
            // Deliberately silent to the caller, so it can't be used to check which emails are registered.
            logger.LogInformation("Password reset requested for an unknown or disabled account.");
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

    // Disabled accounts are locked out "forever"; ordinary lockouts after failed sign-ins last minutes.
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
