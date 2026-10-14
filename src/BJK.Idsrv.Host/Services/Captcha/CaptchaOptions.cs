namespace BJK.Idsrv.Host.Services.Captcha;

public sealed class CaptchaOptions
{
    public const string SectionName = "Captcha";

    /// <summary>Request header the client puts the Cloudflare Turnstile token in.</summary>
    public const string HeaderName = "X-Captcha-Token";

    /// <summary>
    /// Turnstile secret key: Key Vault in Azure, user secrets locally. Locally it's Cloudflare's public
    /// "always passes" test key (see user-secrets.template.json), which accepts only the dummy token XXXX.DUMMY.TOKEN.XXXX.
    /// </summary>
    public string? TurnstileSecretKey { get; set; }

    public Uri TurnstileVerifyUrl { get; set; } = new("https://challenges.cloudflare.com/turnstile/v0/siteverify");
}
