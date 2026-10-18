using Microsoft.AspNetCore.Authentication;

namespace BJK.Idsrv.Host.Services;

/// <summary>
/// Google, Apple and Facebook sign-in. Each is added only if its keys are configured (section
/// <c>Authentication:Google</c>, <c>:Apple</c>, <c>:Facebook</c>), so the login page only shows the buttons that work.
/// Apple requires Sign in with Apple in any iOS app that offers other social sign-in.
/// </summary>
public static class SocialSignIn
{
    public static AuthenticationBuilder AddSocialSignIn(this AuthenticationBuilder auth, IConfiguration configuration)
    {
        var google = configuration.GetSection("Authentication:Google");
        if (!string.IsNullOrWhiteSpace(google["ClientId"]))
        {
            auth.AddGoogle(options =>
            {
                options.ClientId = google["ClientId"]!;
                options.ClientSecret = google["ClientSecret"] ?? string.Empty;
            });
        }

        var facebook = configuration.GetSection("Authentication:Facebook");
        if (!string.IsNullOrWhiteSpace(facebook["AppId"]))
        {
            auth.AddFacebook(options =>
            {
                options.AppId = facebook["AppId"]!;
                options.AppSecret = facebook["AppSecret"] ?? string.Empty;
            });
        }

        var apple = configuration.GetSection("Authentication:Apple");
        if (!string.IsNullOrWhiteSpace(apple["ClientId"]))
        {
            auth.AddApple(options =>
            {
                options.ClientId = apple["ClientId"]!;
                options.TeamId = apple["TeamId"] ?? string.Empty;
                options.KeyId = apple["KeyId"] ?? string.Empty;

                // The .p8 key's contents (PEM). Secret: user secrets locally, Key Vault in Azure.
                var privateKey = apple["PrivateKey"] ?? string.Empty;
                options.GenerateClientSecret = true;
                options.PrivateKey = (_, _) => Task.FromResult(privateKey.AsMemory());
            });
        }

        return auth;
    }
}
