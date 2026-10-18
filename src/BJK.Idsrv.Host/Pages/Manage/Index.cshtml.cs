using BJK.Idsrv.Host.Data;
using BJK.Modules.User.UseCases.Confirm;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Manage;

public sealed class IndexModel(UserManager<BjkIdentityUser> users, ISender sender) : PageModel
{
    public string? Email { get; private set; }

    public bool EmailConfirmed { get; private set; }

    public bool TwoFactorEnabled { get; private set; }

    public bool HasPassword { get; private set; }

    public IList<string> Roles { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        Email = user.Email;
        EmailConfirmed = user.EmailConfirmed;
        TwoFactorEnabled = user.TwoFactorEnabled;
        HasPassword = await users.HasPasswordAsync(user);
        Roles = await users.GetRolesAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostSendConfirmationAsync()
    {
        await sender.Send(new SendEmailConfirmationCommand(), HttpContext.RequestAborted);
        TempData["Notice"] = "We've sent you another confirmation email.";
        return RedirectToPage();
    }
}
