using BJK.Idsrv.Host.Data;
using Duende.IdentityServer.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Account;

/// <summary>Sign-out. When an app starts it (with a logoutId), there's no prompt and the browser goes back to the app.</summary>
public sealed class LogoutModel(SignInManager<BjkIdentityUser> signIn, IIdentityServerInteractionService interaction) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? LogoutId { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return await SignOutAsync();
        }

        var context = await interaction.GetLogoutContextAsync(LogoutId);
        return context?.ShowSignoutPrompt == false ? await SignOutAsync() : Page();
    }

    public Task<IActionResult> OnPostAsync() => SignOutAsync();

    private async Task<IActionResult> SignOutAsync()
    {
        var context = await interaction.GetLogoutContextAsync(LogoutId);
        if (User.Identity?.IsAuthenticated == true)
        {
            await signIn.SignOutAsync();
        }

        var backToApp = context?.PostLogoutRedirectUri;
        return string.IsNullOrEmpty(backToApp) ? RedirectToPage("./LoggedOut") : Redirect(backToApp);
    }
}
