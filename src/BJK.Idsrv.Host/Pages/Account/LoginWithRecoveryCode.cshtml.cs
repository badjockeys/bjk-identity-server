using System.ComponentModel.DataAnnotations;
using BJK.Idsrv.Host.Data;
using BJK.Idsrv.Host.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Account;

public sealed class LoginWithRecoveryCodeModel(SignInManager<BjkIdentityUser> signIn, LoginFlow flow) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty, Required]
    public string RecoveryCode { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync() =>
        await signIn.GetTwoFactorAuthenticationUserAsync() is null ? RedirectToPage("./Login", new { ReturnUrl }) : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            return RedirectToPage("./Login", new { ReturnUrl });
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await signIn.TwoFactorRecoveryCodeSignInAsync(RecoveryCode.Replace(" ", string.Empty, StringComparison.Ordinal));
        if (result.Succeeded)
        {
            return await flow.CompleteAsync(user, ReturnUrl, new PageModelUrls(this));
        }

        ModelState.AddModelError(string.Empty, result.IsLockedOut ? "Too many attempts. Try again in 15 minutes." : "That recovery code isn't right.");
        return Page();
    }
}
