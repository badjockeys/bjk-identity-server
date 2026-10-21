using System.ComponentModel.DataAnnotations;
using BJK.Modules.Safety;
using BJK.Modules.User.UseCases.Reset;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Account;

public sealed class ForgotPasswordModel(ISender sender, ICaptchaVerifier captcha) : PageModel
{
    [BindProperty, Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public bool Sent { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid || !await Request.CaptchaPassedAsync(captcha, ModelState))
        {
            return Page();
        }

        // Always the same answer, so the page can't be used to find out who has an account.
        await sender.Send(new RequestPasswordResetCommand(Email.Trim()), HttpContext.RequestAborted);
        Sent = true;
        return Page();
    }
}
