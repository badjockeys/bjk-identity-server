using System.ComponentModel.DataAnnotations;
using BJK.Modules.User.UseCases.Reset;
using ValidationException = FluentValidation.ValidationException;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Account;

/// <summary>Where the link in the password reset email lands (<c>?email=…&amp;token=…</c>).</summary>
public sealed class ResetPasswordModel(ISender sender) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool Done { get; private set; }

    public IActionResult OnGet(string? email, string? token)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
        {
            return RedirectToPage("./ForgotPassword");
        }

        Input = new InputModel { Email = email, Token = token };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            await sender.Send(new ResetPasswordCommand(Input.Email, Input.Token, Input.NewPassword), HttpContext.RequestAborted);
            Done = true;
        }
        catch (ValidationException ex)
        {
            ModelState.AddErrors(ex);
        }

        return Page();
    }

    public sealed class InputModel
    {
        [Required]
        public string Email { get; set; } = string.Empty;

        [Required]
        public string Token { get; set; } = string.Empty;

        [Required, StringLength(100, MinimumLength = 8, ErrorMessage = "Passwords are at least 8 characters.")]
        public string NewPassword { get; set; } = string.Empty;

        [Required, Compare(nameof(NewPassword), ErrorMessage = "The passwords don't match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
