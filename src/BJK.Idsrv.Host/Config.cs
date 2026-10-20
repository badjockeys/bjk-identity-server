using BJK.Modules.User;
using BJK.Modules.User.Contracts;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;

namespace BJK.Idsrv.Host;

/// <summary>Scope names. The users scopes come from the User module; the others belong to the bjk-core modules.</summary>
public static class Scopes
{
    public const string Users = UserModule.ApiScope;
    public const string UsersManage = UserModule.ManageScope;
    public const string Media = "bjk.media";
    public const string MediaManage = "bjk.media.manage";
    public const string Profiles = "bjk.profiles";
    public const string ProfilesManage = "bjk.profiles.manage";
    public const string Posts = "bjk.posts";
    public const string PostsManage = "bjk.posts.manage";

    /// <summary>What the public apps ask for: everything a signed-in user can do.</summary>
    public static readonly string[] User = [Users, Media, Profiles, Posts];

    /// <summary>What the back-office asks for on top: moderation. Only counts for Moderators and Admins with two-factor sign-in.</summary>
    public static readonly string[] Moderation = [UsersManage, MediaManage, ProfilesManage, PostsManage];
}

/// <summary>Redirect addresses and secret for one app, from configuration (section <c>Clients:Web</c> and so on).</summary>
public sealed class AppClientOptions
{
    public string[] RedirectUris { get; set; } = [];

    public string[] PostLogoutRedirectUris { get; set; } = [];

    /// <summary>Confidential clients only (web, back-office). Secret: user secrets locally, a container secret when deployed.</summary>
    public string? Secret { get; set; }

    public bool IsConfigured => RedirectUris.Length > 0;
}

/// <summary>What this identity server issues tokens for, and to whom.</summary>
public static class Config
{
    /// <summary>Schema for IdentityServer's operational data: signing keys, refresh tokens, sessions.</summary>
    public const string OperationalSchema = "idsrv";

    public const string WebClientId = "bjk-web";
    public const string MobileClientId = "bjk-mobile";
    public const string BackofficeClientId = "bjk-backoffice";

    /// <summary>
    /// Claims every API needs about the user, so they're copied into access tokens. age_verified is only present
    /// (as "true") once the account has passed the age check, and minor (as "true") while the declared date of birth
    /// says the user is under 18 (see AgeAwareProfileService).
    /// </summary>
    private static readonly string[] ApiUserClaims = ["role", "email_verified", AgeVerificationClaims.AgeVerified, AgeVerificationClaims.Minor];

    public static IEnumerable<IdentityResource> IdentityResources =>
    [
        new IdentityResources.OpenId(),
        new IdentityResources.Profile(),
        new IdentityResources.Email(),
        new IdentityResource("roles", "Your staff role", ["role"]),
    ];

    public static IEnumerable<ApiScope> ApiScopes =>
    [
        new(Scopes.Users, "Your Bad Jockeys account"),
        new(Scopes.UsersManage, "Manage Bad Jockeys accounts (staff)"),
        new(Scopes.Media, "Upload and view photos and videos"),
        new(Scopes.MediaManage, "Manage anyone's photos and videos (staff)"),
        new(Scopes.Profiles, "Your public profile"),
        new(Scopes.ProfilesManage, "Moderate public profiles (staff)"),
        new(Scopes.Posts, "Post, react, comment and report"),
        new(Scopes.PostsManage, "Moderate posts, comments and reports (staff)"),
    ];

    /// <summary>
    /// The APIs, as token audiences. The core API (bjk-core) only accepts tokens whose audience includes "bjk-api";
    /// the identity server's own users API checks its scopes in-process.
    /// </summary>
    public static IEnumerable<ApiResource> ApiResources =>
    [
        new("bjk-api", "Bad Jockeys API")
        {
            Scopes = { Scopes.Media, Scopes.MediaManage, Scopes.Profiles, Scopes.ProfilesManage, Scopes.Posts, Scopes.PostsManage },

            // The core API also gets the email address, so it can send report and appeal acknowledgements.
            UserClaims = [.. ApiUserClaims, "email"],
        },
        new("bjk-identity", "Bad Jockeys accounts API")
        {
            Scopes = { Scopes.Users, Scopes.UsersManage },
            UserClaims = [.. ApiUserClaims],
        },
    ];

    public static IEnumerable<Client> Clients(IConfiguration configuration, IHostEnvironment environment)
    {
        var web = configuration.GetSection("Clients:Web").Get<AppClientOptions>() ?? new();
        var mobile = configuration.GetSection("Clients:Mobile").Get<AppClientOptions>() ?? new();
        var backoffice = configuration.GetSection("Clients:Backoffice").Get<AppClientOptions>() ?? new();

        // The public website (Next.js). It signs in on its server, so it can keep a secret.
        if (web.IsConfigured)
        {
            yield return new Client
            {
                ClientId = WebClientId,
                ClientName = "Bad Jockeys",
                AllowedGrantTypes = GrantTypes.Code,
                RequirePkce = true,
                ClientSecrets = { new Secret(Required(web.Secret, "Clients:Web:Secret").Sha256()) },
                RedirectUris = [.. web.RedirectUris],
                PostLogoutRedirectUris = [.. web.PostLogoutRedirectUris],
                RequireConsent = false,
                AllowOfflineAccess = true,
                AllowedScopes = UserScopes(),
                AccessTokenLifetime = (int)TimeSpan.FromMinutes(15).TotalSeconds,
                RefreshTokenUsage = TokenUsage.OneTimeOnly,

                // Re-read the user's claims on every refresh: age_verified appears straight after the age check,
                // and minor lapses on the 18th birthday, without signing in again.
                UpdateAccessTokenClaimsOnRefresh = true,
                RefreshTokenExpiration = TokenExpiration.Sliding,
                SlidingRefreshTokenLifetime = (int)TimeSpan.FromDays(30).TotalSeconds,
                AbsoluteRefreshTokenLifetime = (int)TimeSpan.FromDays(90).TotalSeconds,
            };
        }

        // The phone apps (React Native). A public client: no secret, PKCE, and the system browser for sign-in.
        if (mobile.IsConfigured)
        {
            yield return new Client
            {
                ClientId = MobileClientId,
                ClientName = "Bad Jockeys app",
                AllowedGrantTypes = GrantTypes.Code,
                RequirePkce = true,
                RequireClientSecret = false,
                RedirectUris = [.. mobile.RedirectUris],
                PostLogoutRedirectUris = [.. mobile.PostLogoutRedirectUris],
                RequireConsent = false,
                AllowOfflineAccess = true,
                AllowedScopes = UserScopes(),
                AccessTokenLifetime = (int)TimeSpan.FromMinutes(15).TotalSeconds,
                RefreshTokenUsage = TokenUsage.OneTimeOnly,

                // Re-read the user's claims on every refresh: age_verified appears straight after the age check,
                // and minor lapses on the 18th birthday, without signing in again.
                UpdateAccessTokenClaimsOnRefresh = true,
                RefreshTokenExpiration = TokenExpiration.Sliding,
                SlidingRefreshTokenLifetime = (int)TimeSpan.FromDays(30).TotalSeconds,
                AbsoluteRefreshTokenLifetime = (int)TimeSpan.FromDays(180).TotalSeconds,
            };
        }

        // The back-office (moderation). Only Moderators and Admins can sign in to it, always with two-factor sign-in
        // (enforced on the login page); short sessions.
        if (backoffice.IsConfigured)
        {
            yield return new Client
            {
                ClientId = BackofficeClientId,
                ClientName = "Bad Jockeys back-office",
                AllowedGrantTypes = GrantTypes.Code,
                RequirePkce = true,
                ClientSecrets = { new Secret(Required(backoffice.Secret, "Clients:Backoffice:Secret").Sha256()) },
                RedirectUris = [.. backoffice.RedirectUris],
                PostLogoutRedirectUris = [.. backoffice.PostLogoutRedirectUris],
                RequireConsent = false,
                AllowOfflineAccess = true,
                AllowedScopes = [.. UserScopes(), .. Scopes.Moderation],
                AccessTokenLifetime = (int)TimeSpan.FromMinutes(10).TotalSeconds,
                RefreshTokenUsage = TokenUsage.OneTimeOnly,

                // Re-read the user's claims on every refresh: age_verified appears straight after the age check,
                // and minor lapses on the 18th birthday, without signing in again.
                UpdateAccessTokenClaimsOnRefresh = true,
                RefreshTokenExpiration = TokenExpiration.Sliding,
                SlidingRefreshTokenLifetime = (int)TimeSpan.FromHours(8).TotalSeconds,
                AbsoluteRefreshTokenLifetime = (int)TimeSpan.FromHours(12).TotalSeconds,
                UserSsoLifetime = (int)TimeSpan.FromHours(12).TotalSeconds,
            };
        }

        if (!environment.IsDevelopment())
        {
            yield break;
        }

        // Local testing only. Machine-to-machine admin access, for trying out the moderation endpoints.
        yield return new Client
        {
            ClientId = "bjk-local-admin",
            ClientName = "Local admin tooling (Development only)",
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            ClientSecrets = { new Secret("local-admin-secret".Sha256()) },
            AllowedScopes = [.. Scopes.User, .. Scopes.Moderation],
        };

        // Local testing only. Signs a user in with email + password, without the login page.
        // It may also ask for the moderation scopes, to show they don't work without two-factor sign-in.
        yield return new Client
        {
            ClientId = "bjk-local-user",
            ClientName = "Local user sign-in for testing (Development only)",
            AllowedGrantTypes = GrantTypes.ResourceOwnerPassword,
            ClientSecrets = { new Secret("local-user-secret".Sha256()) },
            AllowedScopes = [.. UserScopes(), .. Scopes.Moderation],
        };
    }

    private static List<string> UserScopes() =>
    [
        IdentityServerConstants.StandardScopes.OpenId,
        IdentityServerConstants.StandardScopes.Profile,
        IdentityServerConstants.StandardScopes.Email,
        IdentityServerConstants.StandardScopes.OfflineAccess,
        "roles",
        .. Scopes.User,
    ];

    private static string Required(string? value, string setting) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{setting} is required when that client's redirect addresses are configured.")
            : value;
}
