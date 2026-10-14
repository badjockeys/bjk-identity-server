using BJK.Modules.User;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;

namespace BJK.Idsrv.Host;

/// <summary>What this identity server issues tokens for, and to whom.</summary>
public static class Config
{
    /// <summary>Schema for IdentityServer's operational data: signing keys, refresh tokens, sessions.</summary>
    public const string OperationalSchema = "idsrv";

    public static IEnumerable<IdentityResource> IdentityResources =>
    [
        new IdentityResources.OpenId(),
        new IdentityResources.Profile(),
        new IdentityResources.Email(),
    ];

    public static IEnumerable<ApiScope> ApiScopes =>
    [
        new(UserModule.ApiScope, "Bad Jockeys users API"),
        new(UserModule.ManageScope, "Manage all Bad Jockeys users"),
    ];

    /// <summary>
    /// The web and mobile app clients are added when those apps are built.
    /// Until then the only clients are two local testing clients, which exist in Development only.
    /// </summary>
    public static IEnumerable<Client> Clients(IHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
        {
            yield break;
        }

        // Machine-to-machine admin access, for trying out the management endpoints.
        yield return new Client
        {
            ClientId = "bjk-local-admin",
            ClientName = "Local admin tooling (Development only)",
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            ClientSecrets = { new Secret("local-admin-secret".Sha256()) },
            AllowedScopes = { UserModule.ApiScope, UserModule.ManageScope },
        };

        // Signs a user in with email + password so the self-service endpoints can be tested without a UI.
        // The password grant is for local testing only; the real apps will use authorization code + PKCE.
        yield return new Client
        {
            ClientId = "bjk-local-user",
            ClientName = "Local user sign-in for testing (Development only)",
            AllowedGrantTypes = GrantTypes.ResourceOwnerPassword,
            ClientSecrets = { new Secret("local-user-secret".Sha256()) },
            AllowedScopes =
            {
                IdentityServerConstants.StandardScopes.OpenId,
                IdentityServerConstants.StandardScopes.Profile,
                IdentityServerConstants.StandardScopes.Email,
                UserModule.ApiScope,
            },
        };
    }
}
