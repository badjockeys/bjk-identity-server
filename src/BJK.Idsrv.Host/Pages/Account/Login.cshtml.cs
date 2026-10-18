using System.ComponentModel.DataAnnotations;
using BJK.Idsrv.Host.Data;
using BJK.Idsrv.Host.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Account;

public sealed class LoginModel(
    SignInManager<BjkIdentityUser> signIn,
    UserManager<BjkIdentityUser> users,
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
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await users.FindByEmailAsync(Input.Email.Trim());
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "That email or password isn't right.");
            return Page();
        }

        var result = await signIn.PasswordSignInAsync(user, Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            return await flow.CompleteAsync(user, ReturnUrl, new PageModelUrls(this));
        }

        if (result.RequiresTwoFactor)
        {
            return RedirectToPage("./LoginWith2fa", new { ReturnUrl, Input.RememberMe });
        }

        if (result.IsLockedOut)
        {
            if (flow.IsDeactivated(user))
            {
                return RedirectToPage("./Reactivate", new { ReturnUrl, email = Input.Email });
            }

            ModelState.AddModelError(string.Empty, "Too many attempts. Try again in 15 minutes, or reset your password.");
            return Page();
        }

        ModelState.AddModelError(string.Empty, "That email or password isn't right.");
        return Page();
    }

    public sealed class InputModel
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; }
    }
}
