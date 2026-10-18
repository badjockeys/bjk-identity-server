using System.ComponentModel.DataAnnotations;
using BJK.Idsrv.Host.Data;
using BJK.Idsrv.Host.Services;
using BJK.Idsrv.Host.Services.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Account;

/// <summary>
/// The second step of sign-in: a code from an authenticator app, or one emailed on request. Staff can't skip it on
/// a remembered device, because their staff rights only count when the session used a second factor.
/// </summary>
public sealed class LoginWith2faModel(
    SignInManager<BjkIdentityUser> signIn,
    UserManager<BjkIdentityUser> users,
    IEmailSender email,
    LoginFlow flow)
    : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool RememberMe { get; set; }

    /// <summary>"app" (authenticator) or "email".</summary>
    [BindProperty(SupportsGet = true)]
    public string Method { get; set; } = "app";

    [BindProperty, Required, StringLength(8, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;

    [BindProperty]
    public bool RememberDevice { get; set; }

    public bool CanRememberDevice { get; private set; }

    public bool CanUseEmail { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            return RedirectToPage("./Login", new { ReturnUrl });
        }

        await LoadAsync(user);

        // People who only use email codes get one straight away.
        if (Method == "app" && await users.GetAuthenticatorKeyAsync(user) is null && CanUseEmail)
        {
            return await OnPostSendEmailAsync();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSendEmailAsync()
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null || string.IsNullOrEmpty(user.Email))
        {
            return RedirectToPage("./Login", new { ReturnUrl });
        }

        var code = await users.GenerateTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider);
        await email.SendAsync(
            new EmailMessage(
                user.Email,
                "Your Bad Jockeys sign-in code",
                $"Your sign-in code is {code}. It expires in a few minutes.\n\nIf you didn't try to sign in, change your password."),
            HttpContext.RequestAborted);

        return RedirectToPage(new { ReturnUrl, RememberMe, Method = "email" });
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            return RedirectToPage("./Login", new { ReturnUrl });
        }

        await LoadAsync(user);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var code = Code.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        var rememberDevice = CanRememberDevice && RememberDevice;
        var result = Method == "email"
            ? await signIn.TwoFactorSignInAsync(TokenOptions.DefaultEmailProvider, code, RememberMe, rememberDevice)
            : await signIn.TwoFactorAuthenticatorSignInAsync(code, RememberMe, rememberDevice);

        if (result.Succeeded)
        {
            return await flow.CompleteAsync(user, ReturnUrl, new PageModelUrls(this));
        }

        ModelState.AddModelError(
            string.Empty,
            result.IsLockedOut ? "Too many attempts. Try again in 15 minutes." : "That code isn't right, or it has expired.");
        return Page();
    }

    private async Task LoadAsync(BjkIdentityUser user)
    {
        CanRememberDevice = !await flow.IsStaffAsync(user);
        CanUseEmail = user.EmailConfirmed;
    }
}
