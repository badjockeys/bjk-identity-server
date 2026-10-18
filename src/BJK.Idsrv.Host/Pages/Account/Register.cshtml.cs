using System.ComponentModel.DataAnnotations;
using BJK.Idsrv.Host.Data;
using BJK.Idsrv.Host.Services;
using BJK.Idsrv.Host.Services.Captcha;
using BJK.Modules.User.UseCases.Add;
using ValidationException = FluentValidation.ValidationException;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Account;

/// <summary>Sign-up through the User module, so the page and the API register people the same way.</summary>
public sealed class RegisterModel(
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

    public IReadOnlyList<AuthenticationScheme> Providers { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Providers = (await signIn.GetExternalAuthenticationSchemesAsync()).ToList();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Providers = (await signIn.GetExternalAuthenticationSchemesAsync()).ToList();
        if (!ModelState.IsValid || !await Request.CaptchaPassedAsync(captcha, ModelState))
        {
            return Page();
        }

        try
        {
            await sender.Send(new AddUserCommand(Input.Email, Input.Password), HttpContext.RequestAborted);
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

        await signIn.SignInAsync(user, isPersistent: false);
        TempData["Notice"] = "Account created. We've emailed you a link to confirm your address.";
        return await flow.CompleteAsync(user, ReturnUrl, new PageModelUrls(this));
    }

    public sealed class InputModel
    {
        [Required, EmailAddress, StringLength(256)]
        public string Email { get; set; } = string.Empty;

        [Required, StringLength(100, MinimumLength = 8, ErrorMessage = "Passwords are at least 8 characters.")]
        public string Password { get; set; } = string.Empty;

        [Required, Compare(nameof(Password), ErrorMessage = "The passwords don't match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
