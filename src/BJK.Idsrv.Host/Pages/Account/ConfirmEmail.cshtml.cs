using BJK.Idsrv.Host.Data;
using BJK.Modules.User.UseCases.Confirm;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Account;

/// <summary>Where the link in the confirmation email lands. Works whether or not the person is signed in.</summary>
public sealed class ConfirmEmailModel(ISender sender, SignInManager<BjkIdentityUser> signIn, UserManager<BjkIdentityUser> users)
    : PageModel
{
    public bool Confirmed { get; private set; }

    public async Task OnGetAsync(Guid userId, string? code)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        try
        {
            await sender.Send(new ConfirmEmailCommand(userId, code), HttpContext.RequestAborted);
            Confirmed = true;
        }
        catch (ValidationException)
        {
            return;
        }

        // Refresh this browser's session so it knows straight away.
        var current = await users.GetUserAsync(User);
        if (current is not null && current.Id == userId.ToString("D"))
        {
            await signIn.RefreshSignInAsync(current);
        }
    }
}
