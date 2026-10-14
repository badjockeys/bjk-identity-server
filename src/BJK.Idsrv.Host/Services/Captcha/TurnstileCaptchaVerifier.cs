using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace BJK.Idsrv.Host.Services.Captcha;

public enum CaptchaResult
{
    Passed,
    Failed,

    /// <summary>The CAPTCHA service couldn't be reached, so the token couldn't be checked.</summary>
    Unavailable,
}

public interface ICaptchaVerifier
{
    Task<CaptchaResult> VerifyAsync(string token, CancellationToken cancellationToken);
}

/// <summary>Checks a Cloudflare Turnstile token with Cloudflare's siteverify endpoint. Tokens are single-use.</summary>
internal sealed class TurnstileCaptchaVerifier(
    HttpClient http,
    IOptions<CaptchaOptions> options,
    ILogger<TurnstileCaptchaVerifier> logger)
    : ICaptchaVerifier
{
    public async Task<CaptchaResult> VerifyAsync(string token, CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["secret"] = options.Value.TurnstileSecretKey ?? string.Empty,
            ["response"] = token,
        });

        try
        {
            using var response = await http.PostAsync(options.Value.TurnstileVerifyUrl, form, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(cancellationToken);
            if (result?.Success == true)
            {
                return CaptchaResult.Passed;
            }

            logger.LogInformation("CAPTCHA rejected: {ErrorCodes}", string.Join(", ", result?.ErrorCodes ?? []));
            return CaptchaResult.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Couldn't reach Cloudflare Turnstile to check a CAPTCHA token.");
            return CaptchaResult.Unavailable;
        }
    }

    private sealed record SiteVerifyResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error-codes")] string[]? ErrorCodes);
}
