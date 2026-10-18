using BJK.Idsrv.Host.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Manage;

/// <summary>Link or unlink Google, Apple and Facebook while signed in. The only way to add a provider to an existing account.</summary>
public sealed class ExternalLoginsModel(UserManager<BjkIdentityUser> users, SignInManager<BjkIdentityUser> signIn) : PageModel
{
    public IList<UserLoginInfo> Linked { get; private set; } = [];

    public IReadOnlyList<AuthenticationScheme> Available { get; private set; } = [];

    /// <summary>A provider can only be unlinked if the account can still be signed in to another way.</summary>
    public bool CanRemove { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostLinkAsync(string provider)
    {
        // Leave any half-finished external sign-in behind, then go to the provider.
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        var callback = Url.Page("./ExternalLogins", pageHandler: "LinkCallback");
        var properties = signIn.ConfigureExternalAuthenticationProperties(provider, callback, users.GetUserId(User));
        return new ChallengeResult(provider, properties);
    }

    public async Task<IActionResult> OnGetLinkCallbackAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var info = await signIn.GetExternalLoginInfoAsync(user.Id);
        if (info is null)
        {
            TempData["Notice"] = "That didn't work. Please try again.";
            return RedirectToPage();
        }

        var result = await users.AddLoginAsync(user, info);
        TempData["Notice"] = result.Succeeded
            ? $"{info.ProviderDisplayName ?? info.LoginProvider} is now linked."
            : "That account is already linked to another Bad Jockeys account.";
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(string loginProvider, string providerKey)
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await LoadAsync(user);
        if (CanRemove)
        {
            await users.RemoveLoginAsync(user, loginProvider, providerKey);
            await signIn.RefreshSignInAsync(user);
            TempData["Notice"] = "Unlinked.";
        }

        return RedirectToPage();
    }

    private async Task LoadAsync(BjkIdentityUser user)
    {
        Linked = await users.GetLoginsAsync(user);
        Available = (await signIn.GetExternalAuthenticationSchemesAsync())
            .Where(s => Linked.All(l => l.LoginProvider != s.Name))
            .ToList();
        CanRemove = await users.HasPasswordAsync(user) || Linked.Count > 1;
    }
}
