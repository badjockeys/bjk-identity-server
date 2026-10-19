using BJK.Modules.User.API.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace BJK.Idsrv.Host.Services.SignUp;

/// <summary>
/// Section <c>SignUp</c>. New accounts can only be created from the listed countries (the UK at launch); viewing is
/// open worldwide, and existing accounts sign in from anywhere.
/// </summary>
public sealed class SignUpOptions
{
    public const string SectionName = "SignUp";

    /// <summary>Cloudflare's header with the visitor's country (ISO 3166-1 alpha-2).</summary>
    public const string CountryHeader = "CF-IPCountry";

    /// <summary>ISO 3166-1 alpha-2 codes where new accounts may be created. Empty means the default, GB.</summary>
    /// <remarks>No initial value here: the configuration binder adds to an array's existing items rather than replacing them.</remarks>
    public string[] AllowedCountries { get; set; } = [];

    /// <summary><see cref="AllowedCountries"/>, or GB if none are set.</summary>
    public IReadOnlyList<string> EffectiveAllowedCountries => AllowedCountries.Length == 0 ? ["GB"] : AllowedCountries;

    /// <summary>
    /// True behind Cloudflare (every deployed environment): the country comes from <see cref="CountryHeader"/>, and
    /// a missing header means no sign-up. Must be true outside Development, since anyone can send the header to a
    /// server Cloudflare doesn't front.
    /// </summary>
    public bool TrustCloudflareCountryHeader { get; set; }

    /// <summary>
    /// Development only, when the header isn't trusted: the country to pretend the visitor is in, e.g. "FR" to see the
    /// refusal. Empty: sign-up is allowed.
    /// </summary>
    public string? DevelopmentCountry { get; set; }
}

/// <summary>The outcome of a sign-up country check.</summary>
/// <param name="Allowed">True if an account may be created.</param>
/// <param name="Country">The country used ("none" if the header was missing, "unchecked" in Development without an override).</param>
public sealed record SignUpCountryDecision(bool Allowed, string Country);

/// <summary>Decides whether a new account may be created from this request's country.</summary>
public sealed class SignUpCountryPolicy(
    IOptions<SignUpOptions> options,
    IHostEnvironment environment,
    ILogger<SignUpCountryPolicy> logger)
{
    /// <summary>Checks the request and logs the country code and outcome (never the IP address).</summary>
    /// <param name="request">The sign-up request.</param>
    public SignUpCountryDecision Check(HttpRequest request)
    {
        var decision = Decide(request.Headers[SignUpOptions.CountryHeader].ToString(), options.Value, environment.IsDevelopment());
        logger.LogInformation(
            "Sign-up country check: {Country} {Outcome}.",
            decision.Country,
            decision.Allowed ? "allowed" : "refused");
        return decision;
    }

    /// <summary>The rule on its own, for tests.</summary>
    /// <param name="header">The CF-IPCountry header value (may be empty).</param>
    /// <param name="settings">The sign-up settings.</param>
    /// <param name="isDevelopment">True in the Development environment.</param>
    public static SignUpCountryDecision Decide(string? header, SignUpOptions settings, bool isDevelopment)
    {
        string? country;
        if (settings.TrustCloudflareCountryHeader)
        {
            country = Normalise(header);
        }
        else if (isDevelopment)
        {
            country = Normalise(settings.DevelopmentCountry);
            if (country is null)
            {
                return new SignUpCountryDecision(true, "unchecked");
            }
        }
        else
        {
            // Startup refuses this combination; refuse rather than trust a header anyone could send.
            return new SignUpCountryDecision(false, "untrusted");
        }

        // Cloudflare sends XX when it doesn't know and T1 for Tor: neither is an allowed country.
        return country is null
            ? new SignUpCountryDecision(false, "none")
            : new SignUpCountryDecision(settings.EffectiveAllowedCountries.Contains(country, StringComparer.OrdinalIgnoreCase), country);
    }

    private static string? Normalise(string? value)
    {
        var trimmed = value?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length != 2 ? null : trimmed;
    }
}

/// <summary>Refuses outside Development unless the Cloudflare header is trusted, and checks the country codes.</summary>
internal sealed class SignUpOptionsValidator(IHostEnvironment environment) : IValidateOptions<SignUpOptions>
{
    public ValidateOptionsResult Validate(string? name, SignUpOptions options)
    {
        var failures = new List<string>();
        if (!environment.IsDevelopment() && !options.TrustCloudflareCountryHeader)
        {
            failures.Add(
                "SignUp:TrustCloudflareCountryHeader must be true outside Development (sign-up is limited by the visitor's country, "
                + "which comes from Cloudflare's CF-IPCountry header).");
        }

        if (!environment.IsDevelopment() && !string.IsNullOrWhiteSpace(options.DevelopmentCountry))
        {
            failures.Add("SignUp:DevelopmentCountry is for local development only.");
        }

        if (options.AllowedCountries.Any(c => c is null || c.Trim().Length != 2))
        {
            failures.Add("SignUp:AllowedCountries must list two-letter country codes, e.g. [\"GB\"].");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Applies the country check to the registration API (<c>POST /api/users</c>), which the apps use; the sign-up pages
/// check it themselves. Runs before the CAPTCHA check, so a refused request doesn't spend a Turnstile token.
/// </summary>
internal sealed class SignUpCountryFilter(SignUpCountryPolicy policy) : IAsyncActionFilter, IOrderedFilter
{
    /// <summary>The message people see when sign-up isn't open where they are.</summary>
    public const string Message = "Bad Jockeys is only open for new accounts in the UK for now. You can still watch posts.";

    public int Order => -100;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is ControllerActionDescriptor action
            && action.ControllerTypeInfo.AsType() == typeof(UserController)
            && action.ActionName == nameof(UserController.Add)
            && !policy.Check(context.HttpContext.Request).Allowed)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Sign-up isn't available in your country yet.",
                Detail = Message,
                Type = "https://badjockeys.com/problems/sign-up-country",
            })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
            return;
        }

        await next();
    }
}

/// <summary>Registers the sign-up country check.</summary>
public static class SignUpServiceCollectionExtensions
{
    /// <summary>Adds <see cref="SignUpCountryPolicy"/>, its settings (checked at startup) and the API filter.</summary>
    /// <param name="services">The services.</param>
    /// <param name="configuration">The app configuration.</param>
    public static IServiceCollection AddSignUpCountryCheck(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SignUpOptions>()
            .Bind(configuration.GetSection(SignUpOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SignUpOptions>, SignUpOptionsValidator>();
        services.AddSingleton<SignUpCountryPolicy>();
        services.Configure<MvcOptions>(mvc => mvc.Filters.Add<SignUpCountryFilter>());
        return services;
    }
}
