using System.ComponentModel.DataAnnotations;
using BJK.Idsrv.Host.Data;
using BJK.Modules.User.UseCases.Deactivate;
using ValidationException = FluentValidation.ValidationException;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Manage;

public sealed class DeactivateModel(ISender sender, SignInManager<BjkIdentityUser> signIn) : PageModel
{
    [BindProperty, Required]
    public string Password { get; set; } = string.Empty;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            await sender.Send(new DeactivateMyAccountCommand(Password), HttpContext.RequestAborted);
        }
        catch (ValidationException ex)
        {
            ModelState.AddErrors(ex, prefix: string.Empty);
            return Page();
        }

        await signIn.SignOutAsync();
        return RedirectToPage("/Account/LoggedOut");
    }
}
