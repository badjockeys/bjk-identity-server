using BJK.Idsrv.Host.Services.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace BJK.Idsrv.Host.Tests;

public sealed class EncryptionTests
{
    private readonly TwoFactorSecretProtector _secrets = new(new EphemeralDataProtectionProvider());

    [Fact]
    public void Two_step_secrets_are_encrypted_and_read_back()
    {
        var stored = _secrets.Protect("JBSWY3DPEHPK3PXP");

        Assert.StartsWith("dp1:", stored);
        Assert.DoesNotContain("JBSWY3DPEHPK3PXP", stored);
        Assert.Equal("JBSWY3DPEHPK3PXP", _secrets.Unprotect(stored));
    }

    [Fact]
    public void Plain_text_from_before_encryption_is_still_read_and_encrypting_twice_changes_nothing()
    {
        Assert.Equal("abc12-def34;ghi56-jkl78", _secrets.Unprotect("abc12-def34;ghi56-jkl78"));
        Assert.Null(_secrets.Unprotect(null));

        var stored = _secrets.Protect("abc12-def34");
        Assert.Equal(stored, _secrets.Protect(stored));
    }

    [Theory]
    [InlineData("[AspNetUserStore]", "AuthenticatorKey", true)]
    [InlineData("[AspNetUserStore]", "RecoveryCodes", true)]
    [InlineData("[AspNetUserStore]", "SomethingElse", false)]
    [InlineData("Google", "AuthenticatorKey", false)]
    public void Only_the_authenticator_key_and_recovery_codes_are_secrets(string loginProvider, string name, bool secret)
    {
        Assert.Equal(secret, TwoFactorSecretProtector.IsSecret(loginProvider, name));
    }

    [Fact]
    public void A_value_encrypted_by_another_key_ring_cannot_be_read()
    {
        var other = new TwoFactorSecretProtector(new EphemeralDataProtectionProvider());

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => other.Unprotect(_secrets.Protect("secret")));
    }

    [Theory]
    [InlineData("Development", false, true)]
    [InlineData("Production", false, false)]
    [InlineData("Production", true, true)]
    public void Outside_development_the_keys_must_be_encrypted_with_a_certificate(string environmentName, bool withCertificate, bool valid)
    {
        var result = Validator(environmentName).Validate(
            null,
            new DataProtectionSettings { Certificate = withCertificate ? Pfx("secret-pass") : null, CertificatePassword = "secret-pass" });

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            Assert.Contains("DataProtection:Certificate is required outside Development", result.FailureMessage);
        }
    }

    [Fact]
    public void A_certificate_that_cant_be_read_stops_startup()
    {
        var wrongPassword = Validator("Production").Validate(
            null, new DataProtectionSettings { Certificate = Pfx("secret-pass"), CertificatePassword = "wrong" });
        var notBase64 = Validator("Production").Validate(null, new DataProtectionSettings { Certificate = "not base64!" });
        var badPrevious = Validator("Production").Validate(
            null,
            new DataProtectionSettings { Certificate = Pfx(null), PreviousCertificates = ["AAAA"] });

        Assert.False(wrongPassword.Succeeded);
        Assert.False(notBase64.Succeeded);
        Assert.Contains("isn't base64", notBase64.FailureMessage);
        Assert.False(badPrevious.Succeeded);
        Assert.Contains("DataProtection:PreviousCertificates:0", badPrevious.FailureMessage);
    }

    [Fact]
    public void Keys_are_stored_encrypted_with_the_certificate()
    {
        var pfx = Pfx(null);
        var protectedKeys = new System.Xml.Linq.XElement("key", "a secret key");

        using var certificate = DataProtectionCertificates.TryLoad(pfx, null, out _)!;
        var encryptor = new Microsoft.AspNetCore.DataProtection.XmlEncryption.CertificateXmlEncryptor(
            certificate, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var encrypted = encryptor.Encrypt(protectedKeys).EncryptedElement;

        Assert.DoesNotContain("a secret key", encrypted.ToString());
        Assert.Contains("X509Certificate", encrypted.ToString());
    }

    private static DataProtectionSettingsValidator Validator(string environmentName)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        return new DataProtectionSettingsValidator(environment);
    }

    /// <summary>A throwaway self-signed certificate as a base64 PFX file.</summary>
    private static string Pfx(string? password)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=bjk-dataprotection-test", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return Convert.ToBase64String(certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, password));
    }
}
