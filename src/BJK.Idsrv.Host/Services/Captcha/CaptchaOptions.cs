namespace BJK.Idsrv.Host.Services.Captcha;

public sealed class CaptchaOptions
{
    public const string SectionName = "Captcha";

    /// <summary>Request header the client puts the Cloudflare Turnstile token in.</summary>
    public const string HeaderName = "X-Captcha-Token";

    /// <summary>
    /// Turnstile secret key: Key Vault in Azure, user secrets locally. Locally it's Cloudflare's public
    /// "always passes" test key (see user-secrets.template.json), which accepts any token; clients send XXXX.DUMMY.TOKEN.XXXX.
    /// Cloudflare's "always fails" test key, 2x0000000000000000000000000000000AA, rejects every token.
    /// </summary>
    public string? TurnstileSecretKey { get; set; }

    /// <summary>
    /// Turnstile site key, used by the widget on the sign-up, forgot password and reactivate pages. Not secret.
    /// Locally it's Cloudflare's "always passes" test site key, 1x00000000000000000000AA.
    /// </summary>
    public string TurnstileSiteKey { get; set; } = "1x00000000000000000000AA";

    /// <summary>The form field the Turnstile widget puts its token in.</summary>
    public const string FormField = "cf-turnstile-response";

    public Uri TurnstileVerifyUrl { get; set; } = new("https://challenges.cloudflare.com/turnstile/v0/siteverify");
}
