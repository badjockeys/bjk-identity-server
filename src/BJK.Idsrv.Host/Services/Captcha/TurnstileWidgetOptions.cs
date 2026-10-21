namespace BJK.Idsrv.Host.Services.Captcha;

/// <summary>
/// The Turnstile widget on the sign-up, forgot password and reactivate pages. Section <c>Captcha</c>, shared with
/// the token check in BJK.Modules.Safety (<c>Captcha:TurnstileSecretKey</c>), so one set of settings serves both.
/// </summary>
public sealed class TurnstileWidgetOptions
{
    public const string SectionName = "Captcha";

    /// <summary>The form field the Turnstile widget puts its token in.</summary>
    public const string FormField = "cf-turnstile-response";

    /// <summary>
    /// Turnstile site key, used by the widget. Not secret. Locally it's Cloudflare's "always passes" test site key,
    /// 1x00000000000000000000AA.
    /// </summary>
    public string TurnstileSiteKey { get; set; } = "1x00000000000000000000AA";
}
