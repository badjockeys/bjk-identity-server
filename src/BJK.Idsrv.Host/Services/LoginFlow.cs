using BJK.Idsrv.Host.Data;
using BJK.Modules.Shared.Security;
using Duende.IdentityServer.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Idsrv.Host.Services;

/// <summary>
/// What happens after someone proves who they are (password, second factor or social sign-in), and the rules the
/// sign-in pages share: staff must use two-factor sign-in, and only staff may sign in to the back-office.
/// </summary>
public sealed class LoginFlow(
    UserManager<BjkIdentityUser> users,
    SignInManager<BjkIdentityUser> signIn,
    IIdentityServerInteractionService interaction,
    TimeProvider clock)
{
    /// <summary>The return address if it's one of ours (an authorize request or a local page), otherwise home.</summary>
    public string SafeReturnUrl(string? returnUrl, IUrlHelper url) =>
        !string.IsNullOrEmpty(returnUrl) && (interaction.IsValidReturnUrl(returnUrl) || url.IsLocalUrl(returnUrl))
            ? returnUrl
            : "~/";

    public async Task<bool> IsStaffAsync(BjkIdentityUser user)
    {
        foreach (var role in StaffRoles.All)
        {
            if (await users.IsInRoleAsync(user, role))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if the sign-in is for the back-office app.</summary>
    public async Task<bool> IsForBackofficeAsync(string? returnUrl) =>
        returnUrl is not null
        && (await interaction.GetAuthorizationContextAsync(returnUrl, CancellationToken.None))?.Client.ClientId == Config.BackofficeClientId;

    /// <summary>
    /// A deactivated account is locked out "forever"; an ordinary lockout after failed attempts lasts minutes.
    /// </summary>
    public bool IsDeactivated(BjkIdentityUser user) =>
        user.LockoutEnd is { } end && end > clock.GetUtcNow().AddYears(100);

    /// <summary>
    /// Called once the user is signed in. Sends staff without two-factor sign-in to set it up, keeps non-staff out of
    /// the back-office, sends brand-new accounts to "Check your age", and otherwise carries on to where the sign-in
    /// started.
    /// </summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="returnUrl">Where the sign-in started.</param>
    /// <param name="page">The page doing the redirect.</param>
    /// <param name="newAccount">True straight after sign-up: the age check comes next (it can be skipped for now).</param>
    public async Task<IActionResult> CompleteAsync(BjkIdentityUser user, string? returnUrl, PageModelUrls page, bool newAccount = false)
    {
        var target = SafeReturnUrl(returnUrl, page.Url);
        var staff = await IsStaffAsync(user);

        if (!staff && await IsForBackofficeAsync(returnUrl))
        {
            await signIn.SignOutAsync();
            return page.RedirectToPage("/Account/AccessDenied");
        }

        if (staff && !user.TwoFactorEnabled)
        {
            return page.RedirectToPage("/Manage/EnableAuthenticator", new { required = true, returnUrl = target });
        }

        if (newAccount)
        {
            return page.RedirectToPage("/Account/CheckAge", new { returnUrl = target });
        }

        return page.Redirect(target);
    }
}

/// <summary>The bits of a page that <see cref="LoginFlow"/> needs to send the browser somewhere.</summary>
public sealed class PageModelUrls(Microsoft.AspNetCore.Mvc.RazorPages.PageModel page)
{
    public IUrlHelper Url => page.Url;

    public IActionResult Redirect(string url) => url == "~/" ? page.LocalRedirect(url) : new RedirectResult(url);

    public IActionResult RedirectToPage(string pageName, object? values = null) => page.RedirectToPage(pageName, values);
}
