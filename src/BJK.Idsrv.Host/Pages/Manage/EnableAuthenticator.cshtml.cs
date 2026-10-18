using System.ComponentModel.DataAnnotations;
using System.Text;
using BJK.Idsrv.Host.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using QRCoder;

namespace BJK.Idsrv.Host.Pages.Manage;

/// <summary>
/// Sets up an authenticator app (TOTP). Staff arrive here straight after signing in if they haven't set up two-step
/// yet (<see cref="Required"/>); afterwards they sign in again, so their session counts as two-step.
/// </summary>
public sealed class EnableAuthenticatorModel(UserManager<BjkIdentityUser> users, SignInManager<BjkIdentityUser> signIn)
    : PageModel
{
    [BindProperty(SupportsGet = true)]
    public bool Required { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty, System.ComponentModel.DataAnnotations.Required, StringLength(7, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;

    public string SharedKey { get; private set; } = string.Empty;

    public string QrSvg { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await LoadKeyAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            await LoadKeyAsync(user);
            return Page();
        }

        var code = Code.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        var valid = await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code);
        if (!valid)
        {
            ModelState.AddModelError(nameof(Code), "That code isn't right. Check the time on your phone and try again.");
            await LoadKeyAsync(user);
            return Page();
        }

        await users.SetTwoFactorEnabledAsync(user, true);
        if (await users.CountRecoveryCodesAsync(user) == 0)
        {
            TempData["RecoveryCodes"] = string.Join(' ', (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!);
        }

        if (Required)
        {
            // Start a fresh session that used two-step, so staff rights count straight away.
            await signIn.SignOutAsync();
            TempData["SignInAgain"] = ReturnUrl ?? "~/";
        }
        else
        {
            await signIn.RefreshSignInAsync(user);
        }

        return RedirectToPage("./ShowRecoveryCodes");
    }

    private async Task LoadKeyAsync(BjkIdentityUser user)
    {
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user) ?? string.Empty;
        }

        SharedKey = FormatKey(key);
        var label = Uri.EscapeDataString($"Bad Jockeys:{user.Email}");
        var uri = $"otpauth://totp/{label}?secret={key}&issuer=Bad%20Jockeys&digits=6";

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        QrSvg = new SvgQRCode(data).GetGraphic(4);
    }

    private static string FormatKey(string key)
    {
        var result = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
        {
            result.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
        }

        return result.ToString().TrimEnd().ToLowerInvariant();
    }
}
