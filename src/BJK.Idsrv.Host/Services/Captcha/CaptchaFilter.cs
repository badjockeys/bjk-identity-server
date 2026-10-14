using BJK.Modules.User.API.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BJK.Idsrv.Host.Services.Captcha;

/// <summary>
/// Requires a valid Turnstile token in the <see cref="CaptchaOptions.HeaderName"/> header on the
/// anonymous endpoints that bots would abuse: registration and "forgot password".
/// Lives in the host rather than the User module, so the module stays free of bot-protection concerns.
/// </summary>
internal sealed class CaptchaFilter(ICaptchaVerifier verifier) : IAsyncActionFilter
{
    private static readonly HashSet<string> ProtectedUserActions =
    [
        nameof(UserController.Add),
        nameof(UserController.RequestPasswordReset),
    ];

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!RequiresCaptcha(context.ActionDescriptor))
        {
            await next();
            return;
        }

        var token = context.HttpContext.Request.Headers[CaptchaOptions.HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(token))
        {
            context.Result = Rejected($"The {CaptchaOptions.HeaderName} header is required.");
            return;
        }

        switch (await verifier.VerifyAsync(token, context.HttpContext.RequestAborted))
        {
            case CaptchaResult.Passed:
                await next();
                break;

            case CaptchaResult.Failed:
                context.Result = Rejected("The CAPTCHA token is invalid or has expired. Get a new one and try again.");
                break;

            default:
                context.Result = new ObjectResult(new ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "The CAPTCHA couldn't be checked right now. Please try again.",
                })
                {
                    StatusCode = StatusCodes.Status503ServiceUnavailable,
                };
                break;
        }
    }

    private static bool RequiresCaptcha(Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor action) =>
        action is ControllerActionDescriptor controllerAction
        && controllerAction.ControllerTypeInfo.AsType() == typeof(UserController)
        && ProtectedUserActions.Contains(controllerAction.ActionName);

    private static BadRequestObjectResult Rejected(string message) =>
        new(new ValidationProblemDetails(new Dictionary<string, string[]>
        {
            [CaptchaOptions.HeaderName] = [message],
        })
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "CAPTCHA check failed.",
        });
}
