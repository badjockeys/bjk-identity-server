using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Manage;

/// <summary>Shows freshly generated recovery codes once. Open to signed-out users too, for staff who must sign in again.</summary>
[AllowAnonymous]
public sealed class ShowRecoveryCodesModel : PageModel
{
    public IReadOnlyList<string> Codes { get; private set; } = [];

    public string? SignInAgain { get; private set; }

    public void OnGet()
    {
        Codes = (TempData["RecoveryCodes"] as string)?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        SignInAgain = TempData["SignInAgain"] as string;
    }
}
