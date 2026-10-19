using BJK.Idsrv.Host.Data;
using BJK.Modules.User.Domain;
using BJK.Modules.User.UseCases.AgeChecks;
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

    public AgeCheckStatus AgeCheckStatus { get; private set; }

    /// <summary>True if the declared date of birth says the user is under 18: the age check isn't offered.</summary>
    public bool UnderAgeByDateOfBirth { get; private set; }

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
        try
        {
            var check = await sender.Send(new GetMyAgeCheckQuery(), HttpContext.RequestAborted);
            AgeCheckStatus = check.Status;
            UnderAgeByDateOfBirth = check.UnderAgeByDateOfBirth;
        }
        catch (BJK.Modules.Shared.Exceptions.NotFoundException)
        {
            // No account record (shouldn't happen): show "not done yet" rather than failing the page.
            AgeCheckStatus = AgeCheckStatus.NotChecked;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSendConfirmationAsync()
    {
        await sender.Send(new SendEmailConfirmationCommand(), HttpContext.RequestAborted);
        TempData["Notice"] = "We've sent you another confirmation email.";
        return RedirectToPage();
    }
}
