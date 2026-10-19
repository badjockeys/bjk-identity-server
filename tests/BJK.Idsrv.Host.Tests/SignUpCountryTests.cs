using BJK.Idsrv.Host.Services.SignUp;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace BJK.Idsrv.Host.Tests;

public sealed class SignUpCountryTests
{
    private static readonly SignUpOptions BehindCloudflare = new() { TrustCloudflareCountryHeader = true };

    [Theory]
    [InlineData("GB", true)]
    [InlineData("gb", true)]
    [InlineData("FR", false)]
    [InlineData("US", false)]
    [InlineData("XX", false)] // Cloudflare doesn't know
    [InlineData("T1", false)] // Tor
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Behind_Cloudflare_only_the_allowed_countries_can_sign_up(string? header, bool allowed)
    {
        Assert.Equal(allowed, SignUpCountryPolicy.Decide(header, BehindCloudflare, isDevelopment: false).Allowed);
    }

    [Fact]
    public void A_missing_header_is_recorded_as_none()
    {
        Assert.Equal(new SignUpCountryDecision(false, "none"), SignUpCountryPolicy.Decide(null, BehindCloudflare, isDevelopment: false));
    }

    [Fact]
    public void The_allowed_list_is_configurable()
    {
        var settings = new SignUpOptions { TrustCloudflareCountryHeader = true, AllowedCountries = ["GB", "IE"] };

        Assert.True(SignUpCountryPolicy.Decide("IE", settings, false).Allowed);
        Assert.False(SignUpCountryPolicy.Decide("FR", settings, false).Allowed);
    }

    [Fact]
    public void GB_is_the_default()
    {
        Assert.Equal(new[] { "GB" }, new SignUpOptions().EffectiveAllowedCountries);
    }

    [Fact]
    public void In_Development_the_header_is_ignored_unless_trusted()
    {
        var settings = new SignUpOptions();

        Assert.True(SignUpCountryPolicy.Decide("FR", settings, isDevelopment: true).Allowed);
        Assert.True(SignUpCountryPolicy.Decide(null, settings, isDevelopment: true).Allowed);
    }

    [Fact]
    public void In_Development_an_override_country_can_be_set()
    {
        Assert.False(SignUpCountryPolicy.Decide(null, new SignUpOptions { DevelopmentCountry = "FR" }, isDevelopment: true).Allowed);
        Assert.True(SignUpCountryPolicy.Decide("FR", new SignUpOptions { DevelopmentCountry = "GB" }, isDevelopment: true).Allowed);
    }

    [Fact]
    public void An_untrusted_header_is_never_believed_outside_Development()
    {
        Assert.False(SignUpCountryPolicy.Decide("GB", new SignUpOptions(), isDevelopment: false).Allowed);
    }

    [Fact]
    public void Startup_requires_the_trusted_header_outside_Development()
    {
        var production = Substitute.For<IHostEnvironment>();
        production.EnvironmentName.Returns("Production");
        var validator = new SignUpOptionsValidator(production);

        Assert.True(validator.Validate(null, new SignUpOptions()).Failed);
        Assert.True(validator.Validate(null, new SignUpOptions { TrustCloudflareCountryHeader = true, DevelopmentCountry = "GB" }).Failed);
        Assert.True(validator.Validate(null, new SignUpOptions { TrustCloudflareCountryHeader = true, AllowedCountries = ["GBR"] }).Failed);
        Assert.True(validator.Validate(null, BehindCloudflare).Succeeded);
    }

    [Fact]
    public void Development_may_run_without_the_header()
    {
        var development = Substitute.For<IHostEnvironment>();
        development.EnvironmentName.Returns("Development");

        Assert.True(new SignUpOptionsValidator(development).Validate(null, new SignUpOptions()).Succeeded);
    }
}
