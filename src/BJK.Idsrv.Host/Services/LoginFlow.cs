using BJK.Idsrv.Host.Data;
using BJK.Modules.Shared.Security;
using BJK.Modules.User.Contracts;
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
    IAccountAgeLookup ages,
    IConfiguration configuration,
    TimeProvider clock)
{
    /// <summary>The return address if it's one of ours (an authorize request or a local page), otherwise home.</summary>
    public string SafeReturnUrl(string? returnUrl, IUrlHelper url) =>
        !string.IsNullOrEmpty(returnUrl) && (interaction.IsValidReturnUrl(returnUrl) || url.IsLocalUrl(returnUrl))
            ? returnUrl
            : "~/";

    /// <summary>
    /// Like <see cref="SafeReturnUrl"/>, but also allows a page of one of our apps (same scheme and host as a redirect
    /// address configured under <c>Clients:Web</c>, <c>Clients:Mobile</c> or <c>Clients:Backoffice</c>), e.g. the web
    /// app's post page that sent the user to verify their age. Anything else goes home.
    /// </summary>
    /// <param name="returnUrl">Where to go back to.</param>
    /// <param name="url">The page's URL helper.</param>
    public string SafeAppReturnUrl(string? returnUrl, IUrlHelper url)
    {
        var safe = SafeReturnUrl(returnUrl, url);
        if (safe != "~/" || !Uri.TryCreate(returnUrl, UriKind.Absolute, out var target))
        {
            return safe;
        }

        var appOrigins = new[] { "Web", "Mobile", "Backoffice" }
            .SelectMany(app => configuration.GetSection($"Clients:{app}:RedirectUris").Get<string[]>() ?? [])
            .Select(address => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null)
            .OfType<string>();
        return appOrigins.Contains(target.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase) ? target.ToString() : safe;
    }

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
    /// Called once the user is signed in. Keeps non-staff out of the back-office, sends staff without two-factor
    /// sign-in to set it up, asks older accounts with no date of birth for it, and otherwise carries on to where the
    /// sign-in started.
    /// </summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="returnUrl">Where the sign-in started.</param>
    /// <param name="page">The page doing the redirect.</param>
    public async Task<IActionResult> CompleteAsync(BjkIdentityUser user, string? returnUrl, PageModelUrls page)
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

        return await FinishSignUpAsync(user, target, page) ?? page.Redirect(target);
    }

    /// <summary>
    /// If the account hasn't finished sign-up (an older account with no date of birth), where to send it: "Your date
    /// of birth". Null once sign-up is finished. Until then the account gets no tokens.
    /// </summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="returnUrl">Where the sign-in started, carried through.</param>
    /// <param name="page">The page doing the redirect.</param>
    public async Task<IActionResult?> FinishSignUpAsync(BjkIdentityUser user, string? returnUrl, PageModelUrls page)
    {
        var facts = Guid.TryParse(user.Id, out var id) ? await ages.GetAsync(id, CancellationToken.None) : null;
        if (facts is null || facts.IsSignUpComplete)
        {
            // No account record means nothing to finish here (tokens are refused anyway).
            return null;
        }

        return page.RedirectToPage("/Account/DateOfBirth", new { returnUrl = SafeReturnUrl(returnUrl, page.Url) });
    }
}

/// <summary>The bits of a page that <see cref="LoginFlow"/> needs to send the browser somewhere.</summary>
public sealed class PageModelUrls(Microsoft.AspNetCore.Mvc.RazorPages.PageModel page)
{
    public IUrlHelper Url => page.Url;

    public IActionResult Redirect(string url) => url == "~/" ? page.LocalRedirect(url) : new RedirectResult(url);

    public IActionResult RedirectToPage(string pageName, object? values = null) => page.RedirectToPage(pageName, values);
}
