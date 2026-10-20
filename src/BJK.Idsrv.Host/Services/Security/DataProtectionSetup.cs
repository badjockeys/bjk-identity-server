using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BJK.Idsrv.Host.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace BJK.Idsrv.Host.Services.Security;

/// <summary>
/// Section <c>DataProtection</c>. The Data Protection keys (which protect cookies, antiforgery tokens, two-step
/// tokens and the stored two-step secrets) are kept in SQL (<c>auth.DataProtectionKeys</c>) so every instance shares
/// them. In deployed environments they're encrypted at rest with an X.509 certificate passed to the container as a
/// secret, so a copy of the database alone can't be used to read them. Standard .NET: no cloud service is involved.
/// </summary>
public sealed class DataProtectionSettings
{
    public const string SectionName = "DataProtection";

    /// <summary>
    /// Secret. The certificate that encrypts the Data Protection keys: a PFX (PKCS #12) file with its private key, as
    /// base64. Required outside Development; the app won't start without it. Locally, empty: keys are stored
    /// unencrypted (with a warning).
    /// </summary>
    public string? Certificate { get; set; }

    /// <summary>Secret. The PFX file's password, if it has one.</summary>
    public string? CertificatePassword { get; set; }

    /// <summary>
    /// Secret. Certificates used before a rotation (base64 PFX files, with <see cref="CertificatePassword"/>), so keys
    /// they encrypted can still be read. New keys always use <see cref="Certificate"/>.
    /// </summary>
    public string[] PreviousCertificates { get; set; } = [];
}

internal sealed class DataProtectionSettingsValidator(IHostEnvironment environment) : IValidateOptions<DataProtectionSettings>
{
    public ValidateOptionsResult Validate(string? name, DataProtectionSettings options)
    {
        if (string.IsNullOrWhiteSpace(options.Certificate))
        {
            return environment.IsDevelopment()
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(
                    "DataProtection:Certificate is required outside Development: the Data Protection keys must be "
                    + "encrypted with a certificate (a PFX file with its private key, as base64).");
        }

        var certificates = new List<(string? Value, string Setting)> { (options.Certificate, "DataProtection:Certificate") };
        certificates.AddRange(options.PreviousCertificates.Select((c, i) => ((string?)c, $"DataProtection:PreviousCertificates:{i}")));
        foreach (var (value, setting) in certificates)
        {
            using var certificate = DataProtectionCertificates.TryLoad(value, options.CertificatePassword, out var error);
            if (certificate is null)
            {
                return ValidateOptionsResult.Fail($"{setting} can't be used: {error}");
            }
        }

        return ValidateOptionsResult.Success;
    }
}

/// <summary>Loads the certificates from their base64 settings.</summary>
internal static class DataProtectionCertificates
{
    /// <summary>The certificate, or null with the reason if it isn't a usable PFX with a private key.</summary>
    public static X509Certificate2? TryLoad(string? base64, string? password, out string? error)
    {
        error = null;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String((base64 ?? string.Empty).Trim());
        }
        catch (FormatException)
        {
            error = "it isn't base64 (encode the .pfx file, e.g. base64 -i dataprotection.pfx).";
            return null;
        }

        try
        {
            // Ephemeral keys aren't written to disk. macOS doesn't support them, so use the default there (local only).
            var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
            var certificate = X509CertificateLoader.LoadPkcs12(bytes, password, flags);
            if (!certificate.HasPrivateKey)
            {
                error = "the PFX file has no private key.";
                certificate.Dispose();
                return null;
            }

            return certificate;
        }
        catch (CryptographicException ex)
        {
            error = $"it couldn't be read as a PFX file ({ex.Message}). Check the password.";
            return null;
        }
    }
}

internal static class DataProtectionSetup
{
    public const string ApplicationName = "bjk-identity-server";

    /// <summary>
    /// Data Protection with keys in SQL, encrypted with the certificate when one is configured. The settings are
    /// checked at startup (required outside Development).
    /// </summary>
    public static IServiceCollection AddBjkDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(DataProtectionSettings.SectionName).Get<DataProtectionSettings>() ?? new();
        var builder = services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToDbContext<BjkIdentityDbContext>();

        // A certificate that doesn't load is reported by the settings check at startup.
        if (DataProtectionCertificates.TryLoad(settings.Certificate, settings.CertificatePassword, out _) is { } current)
        {
            var all = new List<X509Certificate2> { current };
            all.AddRange(settings.PreviousCertificates
                .Select(c => DataProtectionCertificates.TryLoad(c, settings.CertificatePassword, out _))
                .OfType<X509Certificate2>());
            builder.ProtectKeysWithCertificate(current).UnprotectKeysWithAnyCertificate([.. all]);
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
        if (string.IsNullOrWhiteSpace(settings.Certificate) && app.Environment.IsDevelopment())
        {
            app.Logger.LogWarning(
                "Data Protection keys are stored in SQL without encryption at rest. That's fine for local development "
                + "only; deployed environments set DataProtection:Certificate.");
        }
    }
}
