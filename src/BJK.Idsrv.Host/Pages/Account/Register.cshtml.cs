using System.ComponentModel.DataAnnotations;
using BJK.Idsrv.Host.Data;
using BJK.Idsrv.Host.Services;
using BJK.Modules.Safety;
using BJK.Idsrv.Host.Services.SignUp;
using BJK.Modules.User.Domain;
using BJK.Modules.User.UseCases.Add;
using ValidationException = FluentValidation.ValidationException;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Account;

/// <summary>
/// Sign-up through the User module, so the page and the API register people the same way. Asks for the date of
/// birth: under 13 goes to a kind refusal page, with no account created and the date not kept. Bots are stopped by
/// the Turnstile CAPTCHA; there's no selfie at sign-up.
/// </summary>
public sealed class RegisterModel(
    ISender sender,
    SignInManager<BjkIdentityUser> signIn,
    UserManager<BjkIdentityUser> users,
    ICaptchaVerifier captcha,
    LoginFlow flow,
    SignUpCountryPolicy countries,
    TimeProvider clock)
    : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IReadOnlyList<AuthenticationScheme> Providers { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        // New accounts are UK-only for now: say so before anyone fills in the form.
        if (!countries.Check(Request).Allowed)
        {
            return RedirectToPage("./SignUpUnavailable");
        }

        Providers = (await signIn.GetExternalAuthenticationSchemesAsync()).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!countries.Check(Request).Allowed)
        {
            return RedirectToPage("./SignUpUnavailable");
        }

        Providers = (await signIn.GetExternalAuthenticationSchemesAsync()).ToList();
        var today = AgeRules.Today(clock);
        var dateOfBirth = Input.DateOfBirth.Validate(ModelState, "Input.DateOfBirth", today);
        if (!ModelState.IsValid || !await Request.CaptchaPassedAsync(captcha, ModelState))
        {
            return Page();
        }

        if (dateOfBirth is { } date && !AgeRules.IsOldEnoughToJoin(date, today))
        {
            // Nothing is created, and the date of birth goes no further than this request.
            return RedirectToPage("./TooYoung");
        }

        try
        {
            await sender.Send(new AddUserCommand(Input.Email, Input.Password, dateOfBirth), HttpContext.RequestAborted);
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

        public DateOfBirthInput DateOfBirth { get; set; } = new();
    }
}
