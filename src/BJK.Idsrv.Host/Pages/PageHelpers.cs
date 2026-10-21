using BJK.Idsrv.Host.Services.Captcha;
using BJK.Modules.Safety;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BJK.Idsrv.Host.Pages;

/// <summary>The social sign-in buttons partial's model.</summary>
public sealed record ExternalProvidersModel(IReadOnlyList<AuthenticationScheme> Providers, string? ReturnUrl);

internal static class PageHelpers
{
    /// <summary>Checks the Turnstile widget's token from the posted form. Adds a model error if it fails.</summary>
    public static async Task<bool> CaptchaPassedAsync(
        this HttpRequest request,
        ICaptchaVerifier verifier,
        ModelStateDictionary modelState)
    {
        var token = request.Form[TurnstileWidgetOptions.FormField].ToString();
        var result = string.IsNullOrWhiteSpace(token)
            ? CaptchaResult.Failed
            : await verifier.VerifyAsync(token, null, request.HttpContext.RequestAborted);

        switch (result)
        {
            case CaptchaResult.Passed:
                return true;
            case CaptchaResult.Unavailable:
                modelState.AddModelError(string.Empty, "We couldn't check you're human just now. Please try again.");
                return false;
            default:
                modelState.AddModelError(string.Empty, "Please complete the check that you're human.");
                return false;
        }
    }

    /// <summary>Copies a module's validation errors onto the page.</summary>
    public static void AddErrors(this ModelStateDictionary modelState, ValidationException ex, string prefix = "Input")
    {
        foreach (var error in ex.Errors)
        {
            var key = string.IsNullOrEmpty(error.PropertyName) ? string.Empty
                : string.IsNullOrEmpty(prefix) ? error.PropertyName
                : $"{prefix}.{error.PropertyName}";
            modelState.AddModelError(key, error.ErrorMessage);
        }
    }
}
