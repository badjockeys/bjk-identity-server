using BJK.Modules.Safety;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Idsrv.Host.Services.Captcha;

public static class CaptchaServiceCollectionExtensions
{
    /// <summary>
    /// The Turnstile check from BJK.Modules.Safety (<see cref="ICaptchaVerifier"/>, with <c>Captcha:TurnstileSecretKey</c>
    /// required, and a real key required when deployed), the widget's settings for the pages, and the filter that
    /// protects the User module's anonymous endpoints.
    /// </summary>
    public static IServiceCollection AddCaptcha(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCaptchaVerification(configuration);
        services.AddOptions<TurnstileWidgetOptions>().Bind(configuration.GetSection(TurnstileWidgetOptions.SectionName));
        services.Configure<MvcOptions>(mvc => mvc.Filters.Add<CaptchaFilter>());

        return services;
    }
}
