using System.Security.Claims;
using BJK.Idsrv.Host.Data;
using BJK.Idsrv.Host.Services;
using BJK.Idsrv.Host.Services.SignUp;
using BJK.Modules.User.Domain;
using BJK.Modules.User.UseCases.Add;
using BJK.Modules.User.UseCases.Reactivate;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Account;

/// <summary>
/// Google, Apple and Facebook sign-in. A first sign-in asks for the date of birth (under 13 is refused, with nothing
/// created or kept), then creates an account with the provider's (verified) email.
/// If an account with that email already exists, it is never linked automatically: the person signs in with their
/// password and links the provider from their account page, so nobody can take over an account through a provider.
/// </summary>
public sealed class ExternalLoginModel(
    SignInManager<BjkIdentityUser> signIn,
    UserManager<BjkIdentityUser> users,
    ISender sender,
    LoginFlow flow,
    SignUpCountryPolicy countries,
    TimeProvider clock,
    ILogger<ExternalLoginModel> logger)
    : PageModel
{
    public enum Result
    {
        Failed,
        Deactivated,
        EmailInUse,
        NoEmail,
        NeedsDateOfBirth,
    }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public Result Outcome { get; private set; }

    public string? ProviderName { get; private set; }

    public string? Email { get; private set; }

    /// <summary>The date of birth asked before a first social sign-in creates an account.</summary>
    [BindProperty]
    public DateOfBirthInput DateOfBirth { get; set; } = new();

    public IActionResult OnGet() => RedirectToPage("./Login");

    /// <summary>The provider button: off to Google, Apple or Facebook.</summary>
    public IActionResult OnPost(string provider)
    {
        var callback = Url.Page("./ExternalLogin", pageHandler: "Callback", values: new { ReturnUrl });
        var properties = signIn.ConfigureExternalAuthenticationProperties(provider, callback);
        return new ChallengeResult(provider, properties);
    }

    public async Task<IActionResult> OnGetCallbackAsync(string? remoteError)
    {
        if (remoteError is not null)
        {
            logger.LogInformation("External sign-in failed at the provider: {Error}", remoteError);
            return Page();
        }

        var info = await signIn.GetExternalLoginInfoAsync();
        if (info is null)
        {
            return Page();
        }

        ProviderName = info.ProviderDisplayName ?? info.LoginProvider;
        var result = await signIn.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: false, bypassTwoFactor: false);
        var existing = await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey);

        if (result.Succeeded && existing is not null)
        {
            return await flow.CompleteAsync(existing, ReturnUrl, new PageModelUrls(this));
        }

        if (result.RequiresTwoFactor)
        {
            return RedirectToPage("./LoginWith2fa", new { ReturnUrl, RememberMe = false });
        }

        if (result.IsLockedOut && existing is not null && flow.IsDeactivated(existing))
        {
            Outcome = Result.Deactivated;
            return Page();
        }

        if (existing is not null)
        {
            return Page();
        }

        // First time with this provider: ask for the date of birth before creating an account, unless the email
        // already has one or sign-up isn't open here.
        var refusal = await CheckNewAccountAsync(info);
        if (refusal is not null)
        {
            return refusal;
        }

        Outcome = Result.NeedsDateOfBirth;
        return Page();
    }

    /// <summary>The date of birth form, before a first social sign-in creates the account.</summary>
    public async Task<IActionResult> OnPostCreateAccountAsync()
    {
        var info = await signIn.GetExternalLoginInfoAsync();
        if (info is null)
        {
            return RedirectToPage("./Login", new { ReturnUrl });
        }

        ProviderName = info.ProviderDisplayName ?? info.LoginProvider;
        if (await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey) is not null)
        {
            // Created meanwhile (another tab): sign in as usual.
            return await OnGetCallbackAsync(remoteError: null);
        }

        var refusal = await CheckNewAccountAsync(info);
        if (refusal is not null)
        {
            return refusal;
        }

        var today = AgeRules.Today(clock);
        var dateOfBirth = DateOfBirth.Validate(ModelState, nameof(DateOfBirth), today);
        if (dateOfBirth is not { } date)
        {
            Outcome = Result.NeedsDateOfBirth;
            return Page();
        }

        if (!AgeRules.IsOldEnoughToJoin(date, today))
        {
            // Nothing is created, and the date of birth goes no further than this request.
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            return RedirectToPage("./TooYoung");
        }

        var user = new BjkIdentityUser { UserName = Email, Email = Email, EmailConfirmed = true };
        var created = await users.CreateAsync(user);
        if (!created.Succeeded || !(await users.AddLoginAsync(user, info)).Succeeded)
        {
            logger.LogWarning("Couldn't create an account from {Provider}: {Errors}", info.LoginProvider, string.Join(" ", created.Errors.Select(e => e.Description)));
            return Page();
        }

        try
        {
            await sender.Send(new AddExternalUserCommand(user.Id, date), HttpContext.RequestAborted);
        }
        catch
        {
            // Don't leave a sign-in account with no account record behind.
            await users.DeleteAsync(user);
            throw;
        }

        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        await signIn.SignInAsync(user, isPersistent: false, info.LoginProvider);
        return await flow.CompleteAsync(user, ReturnUrl, new PageModelUrls(this));
    }

    /// <summary>
    /// Why a first social sign-in can't create an account, as the page to show; null if it can. Sets
    /// <see cref="Email"/> from the provider.
    /// </summary>
    private async Task<IActionResult?> CheckNewAccountAsync(ExternalLoginInfo info)
    {
        Email = info.Principal.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(Email))
        {
            Outcome = Result.NoEmail;
            return Page();
        }

        if (await users.FindByEmailAsync(Email) is not null)
        {
            Outcome = Result.EmailInUse;
            return Page();
        }

        // New accounts are UK-only for now. Existing accounts sign in from anywhere.
        if (!countries.Check(Request).Allowed)
        {
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            return RedirectToPage("./SignUpUnavailable");
        }

        return null;
    }

    /// <summary>Reactivates a deactivated account the person just proved is theirs by signing in with the provider.</summary>
    public async Task<IActionResult> OnPostReactivateAsync()
    {
        var info = await signIn.GetExternalLoginInfoAsync();
        var user = info is null ? null : await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
        if (info is null || user is null)
        {
            return RedirectToPage("./Login", new { ReturnUrl });
        }

        await sender.Send(new ReactivateVerifiedAccountCommand(Guid.Parse(user.Id)), HttpContext.RequestAborted);

        var result = await signIn.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: false, bypassTwoFactor: false);
        if (result.RequiresTwoFactor)
        {
            return RedirectToPage("./LoginWith2fa", new { ReturnUrl, RememberMe = false });
        }

        return result.Succeeded
            ? await flow.CompleteAsync(user, ReturnUrl, new PageModelUrls(this))
            : RedirectToPage("./Login", new { ReturnUrl });
    }
}
