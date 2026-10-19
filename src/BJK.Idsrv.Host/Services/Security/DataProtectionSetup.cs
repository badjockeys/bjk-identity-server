using Azure.Identity;
using BJK.Idsrv.Host.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace BJK.Idsrv.Host.Services.Security;

/// <summary>
/// Section <c>DataProtection</c>. The Data Protection keys (which protect cookies, antiforgery tokens, two-step
/// tokens and the stored two-step secrets) are kept in SQL (<c>auth.DataProtectionKeys</c>) so every instance shares
/// them. In deployed environments they're encrypted at rest with an Azure Key Vault key, using the app's managed
/// identity, so a copy of the database alone can't be used to read them.
/// </summary>
public sealed class DataProtectionSettings
{
    public const string SectionName = "DataProtection";

    /// <summary>
    /// The Key Vault key that wraps the Data Protection keys, e.g.
    /// <c>https://bjk-live-kv.vault.azure.net/keys/dataprotection</c> (optionally with a version). Required outside
    /// Development; the app won't start without it. Locally, empty: keys are stored unencrypted (with a warning).
    /// </summary>
    public string? KeyVaultKeyId { get; set; }

    /// <summary>
    /// Optional: the client id of a user-assigned managed identity to use for Key Vault. Empty uses
    /// DefaultAzureCredential's normal chain (the system-assigned managed identity in Azure).
    /// </summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>The key id as a URL, if it's a valid Key Vault key URL.</summary>
    public Uri? KeyVaultKeyUri =>
        Uri.TryCreate(KeyVaultKeyId, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.AbsolutePath.StartsWith("/keys/", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Length > "/keys/".Length
            ? uri
            : null;
}

internal sealed class DataProtectionSettingsValidator(IHostEnvironment environment) : IValidateOptions<DataProtectionSettings>
{
    public ValidateOptionsResult Validate(string? name, DataProtectionSettings options)
    {
        if (string.IsNullOrWhiteSpace(options.KeyVaultKeyId))
        {
            return environment.IsDevelopment()
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(
                    "DataProtection:KeyVaultKeyId is required outside Development: the Data Protection keys must be "
                    + "encrypted with an Azure Key Vault key (e.g. https://<vault>.vault.azure.net/keys/<key>).");
        }

        return options.KeyVaultKeyUri is null
            ? ValidateOptionsResult.Fail(
                "DataProtection:KeyVaultKeyId must be a Key Vault key URL, e.g. https://<vault>.vault.azure.net/keys/<key>.")
            : ValidateOptionsResult.Success;
    }
}

internal static class DataProtectionSetup
{
    public const string ApplicationName = "bjk-identity-server";

    /// <summary>
    /// Data Protection with keys in SQL, wrapped with the Key Vault key when one is configured. The settings are
    /// checked at startup (required outside Development).
    /// </summary>
    public static IServiceCollection AddBjkDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(DataProtectionSettings.SectionName).Get<DataProtectionSettings>() ?? new();
        var builder = services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToDbContext<BjkIdentityDbContext>();

        if (settings.KeyVaultKeyUri is { } keyUri)
        {
            var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = string.IsNullOrWhiteSpace(settings.ManagedIdentityClientId) ? null : settings.ManagedIdentityClientId,
            });
            builder.ProtectKeysWithAzureKeyVault(keyUri, credential);
        }

        services.AddOptions<DataProtectionSettings>()
            .Bind(configuration.GetSection(DataProtectionSettings.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DataProtectionSettings>, DataProtectionSettingsValidator>();
        return services;
    }

    /// <summary>Says so in the log when the keys aren't encrypted (only allowed in Development).</summary>
    public static void WarnIfUnprotected(this WebApplication app)
    {
        // Raw configuration, not IOptions: reading the options here would run their startup check early, before
        // the other settings are checked with it.
        var settings = app.Configuration.GetSection(DataProtectionSettings.SectionName).Get<DataProtectionSettings>() ?? new();
        if (settings.KeyVaultKeyUri is null && app.Environment.IsDevelopment())
        {
            app.Logger.LogWarning(
                "Data Protection keys are stored in SQL without encryption at rest. That's fine for local development "
                + "only; deployed environments set DataProtection:KeyVaultKeyId.");
        }
    }
}
