using Microsoft.AspNetCore.Mvc;

namespace BJK.Idsrv.Host.Services.Captcha;

public static class CaptchaServiceCollectionExtensions
{
    public static IServiceCollection AddCaptcha(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CaptchaOptions>()
            .Bind(configuration.GetSection(CaptchaOptions.SectionName))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.TurnstileSecretKey),
                "Captcha:TurnstileSecretKey is required. Locally, load user-secrets.template.json into your user secrets; in Azure it comes from Key Vault.")
            .ValidateOnStart();

        services.AddHttpClient<ICaptchaVerifier, TurnstileCaptchaVerifier>(http => http.Timeout = TimeSpan.FromSeconds(10));
        services.Configure<MvcOptions>(mvc => mvc.Filters.Add<CaptchaFilter>());

        return services;
    }
}
