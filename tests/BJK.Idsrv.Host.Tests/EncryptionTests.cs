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
    [InlineData("Development", "", true)]
    [InlineData("Production", "", false)]
    [InlineData("Production", "https://bjk-live-kv.vault.azure.net/keys/dataprotection", true)]
    [InlineData("Production", "https://bjk-live-kv.vault.azure.net/keys/dataprotection/0123456789abcdef", true)]
    [InlineData("Production", "http://bjk-live-kv.vault.azure.net/keys/dataprotection", false)]
    [InlineData("Production", "https://bjk-live-kv.vault.azure.net/secrets/dataprotection", false)]
    [InlineData("Development", "not a url", false)]
    public void A_Key_Vault_key_is_required_outside_Development(string environmentName, string keyId, bool valid)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);

        var result = new DataProtectionSettingsValidator(environment).Validate(null, new DataProtectionSettings { KeyVaultKeyId = keyId });

        Assert.Equal(valid, result.Succeeded);
        if (!valid && keyId.Length == 0)
        {
            Assert.Contains("DataProtection:KeyVaultKeyId is required outside Development", result.FailureMessage);
        }
    }
}
