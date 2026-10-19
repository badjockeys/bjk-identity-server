using BJK.Idsrv.Host.Data;
using BJK.Idsrv.Host.Services;
using BJK.Idsrv.Host.Services.SignUp;
using BJK.Modules.User.Domain;
using BJK.Modules.User.UseCases.SignUp;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ValidationException = FluentValidation.ValidationException;

namespace BJK.Idsrv.Host.Pages.Account;

/// <summary>
/// "Your date of birth", for accounts created before dates of birth were asked for: they get no tokens until they
/// give it. Under 13 goes to the refusal page and is signed out; the date isn't kept.
/// </summary>
public sealed class DateOfBirthModel(
    ISender sender,
    SignInManager<BjkIdentityUser> signIn,
    LoginFlow flow,
    TimeProvider clock)
    : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public DateOfBirthInput DateOfBirth { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        var status = await sender.Send(new GetMySignUpQuery(), HttpContext.RequestAborted);
        return status.HasDateOfBirth ? Redirect(Url.Content(flow.SafeReturnUrl(ReturnUrl, Url))) : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var today = AgeRules.Today(clock);
        if (DateOfBirth.Validate(ModelState, nameof(DateOfBirth), today) is not { } date)
        {
            return Page();
        }

        if (!AgeRules.IsOldEnoughToJoin(date, today))
        {
            await signIn.SignOutAsync();
            return RedirectToPage("./TooYoung");
        }

        try
        {
            await sender.Send(new SetMyDateOfBirthCommand(date), HttpContext.RequestAborted);
        }
        catch (ValidationException ex)
        {
            ModelState.AddErrors(ex, prefix: string.Empty);
            return Page();
        }

        var target = flow.SafeReturnUrl(ReturnUrl, Url);
        return target == "~/" ? LocalRedirect(target) : Redirect(target);
    }
}
