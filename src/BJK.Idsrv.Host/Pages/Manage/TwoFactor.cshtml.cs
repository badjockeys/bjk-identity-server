using BJK.Idsrv.Host.Data;
using BJK.Idsrv.Host.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Manage;

public sealed class TwoFactorModel(UserManager<BjkIdentityUser> users, SignInManager<BjkIdentityUser> signIn, LoginFlow flow)
    : PageModel
{
    public bool Enabled { get; private set; }

    public bool HasAuthenticator { get; private set; }

    public bool EmailConfirmed { get; private set; }

    public bool IsStaff { get; private set; }

    public int RecoveryCodesLeft { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        Enabled = user.TwoFactorEnabled;
        HasAuthenticator = await users.GetAuthenticatorKeyAsync(user) is not null;
        EmailConfirmed = user.EmailConfirmed;
        IsStaff = await flow.IsStaffAsync(user);
        RecoveryCodesLeft = await users.CountRecoveryCodesAsync(user);
        return Page();
    }

    /// <summary>Two-step with emailed codes only (no app).</summary>
    public async Task<IActionResult> OnPostEnableEmailAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        if (!user.EmailConfirmed)
        {
            return RedirectToPage();
        }

        await users.SetTwoFactorEnabledAsync(user, true);
        await signIn.RefreshSignInAsync(user);
        TempData["RecoveryCodes"] = string.Join(' ', (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!);
        return RedirectToPage("./ShowRecoveryCodes");
    }

    public async Task<IActionResult> OnPostNewRecoveryCodesAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null || !user.TwoFactorEnabled)
        {
            return RedirectToPage();
        }

        TempData["RecoveryCodes"] = string.Join(' ', (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!);
        return RedirectToPage("./ShowRecoveryCodes");
    }

    public async Task<IActionResult> OnPostDisableAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        if (await flow.IsStaffAsync(user))
        {
            TempData["Notice"] = "Moderators and admins can't turn off two-step verification.";
            return RedirectToPage();
        }

        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        await signIn.RefreshSignInAsync(user);
        TempData["Notice"] = "Two-step verification is off.";
        return RedirectToPage();
    }
}
