using System.ComponentModel.DataAnnotations;
using BJK.Idsrv.Host.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Manage;

public sealed class ChangePasswordModel(UserManager<BjkIdentityUser> users, SignInManager<BjkIdentityUser> signIn) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool HasPassword { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        HasPassword = await users.HasPasswordAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        HasPassword = await users.HasPasswordAsync(user);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (HasPassword && string.IsNullOrEmpty(Input.CurrentPassword))
        {
            ModelState.AddModelError("Input.CurrentPassword", "Enter your current password.");
            return Page();
        }

        var result = HasPassword
            ? await users.ChangePasswordAsync(user, Input.CurrentPassword!, Input.NewPassword)
            : await users.AddPasswordAsync(user, Input.NewPassword);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return Page();
        }

        await signIn.RefreshSignInAsync(user);
        TempData["Notice"] = "Your password has been saved.";
        return RedirectToPage("./Index");
    }

    public sealed class InputModel
    {
        public string? CurrentPassword { get; set; }

        [Required, StringLength(100, MinimumLength = 8, ErrorMessage = "Passwords are at least 8 characters.")]
        public string NewPassword { get; set; } = string.Empty;

        [Required, Compare(nameof(NewPassword), ErrorMessage = "The passwords don't match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
