using BJK.Idsrv.Host.Data;
using BJK.Idsrv.Host.Services;
using BJK.Idsrv.Host.Services.Support;
using BJK.Modules.User;
using BJK.Modules.User.API.Controllers;
using BJK.Modules.User.Domain;
using BJK.Modules.User.UseCases.AgeChecks;
using BJK.Modules.User.UseCases.SignUp;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using ValidationException = FluentValidation.ValidationException;

namespace BJK.Idsrv.Host.Pages.Account;

/// <summary>
/// "Verify your age to view sensitive posts" (<c>/Account/CheckAge?returnUrl=...</c>): the highly effective age
/// check, on demand. The web app sends a signed-in user here when they tap a sensitive post; afterwards they go back
/// to <c>returnUrl</c> (one of our apps' pages) and their next token (or refresh) carries age_verified. A 3–5 second
/// head-turn selfie recorded in the browser (liveness against spoofing, then the face age estimate), or a photo of an
/// ID instead. If the declared date of birth says they're under 18, the page explains that sensitive posts are for
/// adults and offers nothing to check. The upload is read into memory (the limits below keep ASP.NET from buffering
/// it to disk), checked through the User module and wiped; only the outcome is kept.
/// </summary>
[RequestSizeLimit(AgeCheckUploads.MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = AgeCheckUploads.MaxRequestBytes, MemoryBufferThreshold = AgeCheckUploads.MaxRequestBytes)]
public sealed class CheckAgeModel(
    ISender sender,
    LoginFlow flow,
    IOptions<AgeAssuranceOptions> options,
    IOptions<SupportOptions> support,
    UserManager<BjkIdentityUser> users)
    : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>"document" shows the photo-ID route.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Method { get; set; }

    public AgeCheckDto Check { get; private set; } = null!;

    public string? Error { get; private set; }

    public bool ShowDocument => string.Equals(Method, "document", StringComparison.OrdinalIgnoreCase)
        || (!Check.CanUseFaceCheck && Check.CanUseDocumentCheck);

    public string ContinueUrl => Url.Content(flow.SafeAppReturnUrl(ReturnUrl, Url));

    public int ChallengeAge => options.Value.ChallengeAge;

    /// <summary>The support address (<c>Support:Email</c>).</summary>
    public string SupportEmail => support.Value.Email;

    /// <summary>
    /// "Contact support" mailto link, when the check failed, couldn't verify the user, or they're out of tries.
    /// Null otherwise.
    /// </summary>
    public string? SupportUrl { get; private set; }

    private async Task SetSupportAsync()
    {
        SupportUrl = Check is { NeedsSupport: true, Reference: { } reference }
            ? SupportLink.ForAgeCheck(SupportEmail, reference, (await users.GetUserAsync(User))?.Email)
            : null;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        // Older accounts give their date of birth first: it decides whether the check is offered at all.
        if (!(await sender.Send(new GetMySignUpQuery(), HttpContext.RequestAborted)).HasDateOfBirth)
        {
            return RedirectToPage("./DateOfBirth", new { returnUrl = Url.Page("./CheckAge", new { ReturnUrl }) });
        }

        Check = await sender.Send(new GetMyAgeCheckQuery(), HttpContext.RequestAborted);
        await SetSupportAsync();
        return Page();
    }

    /// <summary>The recorded selfie, posted by the page's script. Answers JSON for the script to show.</summary>
    public async Task<IActionResult> OnPostVideoAsync(IFormFile? video)
    {
        try
        {
            var (bytes, contentType) = await AgeCheckUploads.ReadAsync(video, HttpContext.RequestAborted);
            var check = await sender.Send(new SubmitFaceAgeCheckCommand(bytes, contentType), HttpContext.RequestAborted);
            return new JsonResult(new
            {
                status = check.Status.ToString(),
                passed = check.Status == AgeCheckStatus.Passed,
                message = check.Message,
                canUseFaceCheck = check.CanUseFaceCheck,
                canUseDocumentCheck = check.CanUseDocumentCheck,
                attemptsLeft = check.AttemptsLeft,

                // The page reloads to show "Contact support" with the reference.
                needsSupport = check.NeedsSupport,
            });
        }
        catch (ValidationException ex)
        {
            // E.g. out of tries for today: the page reloads to offer "Contact support" if that applies now.
            var current = await sender.Send(new GetMyAgeCheckQuery(), HttpContext.RequestAborted);
            return new JsonResult(new
            {
                message = string.Join(" ", ex.Errors.Select(e => e.ErrorMessage)),
                needsSupport = current.NeedsSupport,
            })
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }
    }

    /// <summary>The ID photo, from the photo-ID form.</summary>
    public async Task<IActionResult> OnPostDocumentAsync(IFormFile? image)
    {
        Method = "document";
        try
        {
            var (bytes, contentType) = await AgeCheckUploads.ReadAsync(image, HttpContext.RequestAborted);
            Check = await sender.Send(new SubmitDocumentAgeCheckCommand(bytes, contentType), HttpContext.RequestAborted);
        }
        catch (ValidationException ex)
        {
            Error = string.Join(" ", ex.Errors.Select(e => e.ErrorMessage));
            Check = await sender.Send(new GetMyAgeCheckQuery(), HttpContext.RequestAborted);
        }

        await SetSupportAsync();
        return Page();
    }
}
