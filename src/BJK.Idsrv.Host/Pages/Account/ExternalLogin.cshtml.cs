using System.Security.Claims;
using BJK.Idsrv.Host.Data;
using BJK.Idsrv.Host.Services;
using BJK.Modules.User.UseCases.Add;
using BJK.Modules.User.UseCases.Reactivate;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages.Account;

/// <summary>
/// Google, Apple and Facebook sign-in. A first sign-in creates an account with the provider's (verified) email.
/// If an account with that email already exists, it is never linked automatically: the person signs in with their
/// password and links the provider from their account page, so nobody can take over an account through a provider.
/// </summary>
public sealed class ExternalLoginModel(
    SignInManager<BjkIdentityUser> signIn,
    UserManager<BjkIdentityUser> users,
    ISender sender,
    LoginFlow flow,
    ILogger<ExternalLoginModel> logger)
    : PageModel
{
    public enum Result
    {
        Failed,
        Deactivated,
        EmailInUse,
        NoEmail,
    }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public Result Outcome { get; private set; }

    public string? ProviderName { get; private set; }

    public string? Email { get; private set; }

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

        // First time with this provider: create an account, unless the email already has one.
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

        var user = new BjkIdentityUser { UserName = Email, Email = Email, EmailConfirmed = true };
        var created = await users.CreateAsync(user);
        if (!created.Succeeded || !(await users.AddLoginAsync(user, info)).Succeeded)
        {
            logger.LogWarning("Couldn't create an account from {Provider}: {Errors}", info.LoginProvider, string.Join(" ", created.Errors.Select(e => e.Description)));
            return Page();
        }

        await sender.Send(new AddExternalUserCommand(user.Id), HttpContext.RequestAborted);
        await signIn.SignInAsync(user, isPersistent: false, info.LoginProvider);
        return await flow.CompleteAsync(user, ReturnUrl, new PageModelUrls(this));
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
