using System.ComponentModel.DataAnnotations;
using BJK.Idsrv.Host.Data;
using BJK.Idsrv.Host.Services;
using BJK.Idsrv.Host.Services.Captcha;
using BJK.Modules.User.UseCases.Reactivate;
using ValidationException = FluentValidation.ValidationException;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Account;

public sealed class ReactivateModel(
    ISender sender,
    SignInManager<BjkIdentityUser> signIn,
    UserManager<BjkIdentityUser> users,
    ICaptchaVerifier captcha,
    LoginFlow flow)
    : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public void OnGet(string? email)
    {
        Input.Email = email ?? string.Empty;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid || !await Request.CaptchaPassedAsync(captcha, ModelState))
        {
            return Page();
        }

        try
        {
            await sender.Send(new ReactivateAccountCommand(Input.Email, Input.Password), HttpContext.RequestAborted);
        }
        catch (ValidationException ex)
        {
            ModelState.AddErrors(ex);
            return Page();
        }

        var user = await users.FindByEmailAsync(Input.Email.Trim());
        if (user is null)
        {
            return RedirectToPage("./Login", new { ReturnUrl });
        }

        var result = await signIn.PasswordSignInAsync(user, Input.Password, isPersistent: false, lockoutOnFailure: true);
        if (result.RequiresTwoFactor)
        {
            return RedirectToPage("./LoginWith2fa", new { ReturnUrl, RememberMe = false });
        }

        return result.Succeeded
            ? await flow.CompleteAsync(user, ReturnUrl, new PageModelUrls(this))
            : RedirectToPage("./Login", new { ReturnUrl });
    }

    public sealed class InputModel
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;
    }
}
