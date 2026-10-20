using BJK.Idsrv.Host.Data;
using BJK.Idsrv.Host.Services;
using Xunit;

namespace BJK.Idsrv.Host.Tests;

public sealed class MobileNumberTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 21, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("07700 900123", "+447700900123")]
    [InlineData("07700-900-123", "+447700900123")]
    [InlineData("+44 7700 900123", "+447700900123")]
    [InlineData("+44 (0)7700 900123", null)]
    [InlineData("447700900123", "+447700900123")]
    [InlineData("0044 7700 900123", "+447700900123")]
    [InlineData("07624 123456", "+447624123456")]
    [InlineData("0151 123 4567", null)]
    [InlineData("07012 345678", null)]
    [InlineData("07612 345678", null)]
    [InlineData("0770090012", null)]
    [InlineData("+1 415 555 0100", null)]
    [InlineData("call 07700900123", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_UK_mobile_numbers_are_accepted_in_E164(string? input, string? expected) =>
        Assert.Equal(expected, MobileNumbers.Normalise(input));

    [Fact]
    public void Numbers_are_masked_for_the_account_page() =>
        Assert.Equal("07700 *** 123", MobileNumbers.Mask("+447700900123"));

    [Fact]
    public void Codes_can_be_texted_once_a_minute_and_five_times_a_day()
    {
        var user = new BjkIdentityUser();
        Assert.Null(MobileCodeLimits.Problem(user, Now));

        MobileCodeLimits.Sent(user, Now);
        Assert.Contains("Wait a minute", MobileCodeLimits.Problem(user, Now.AddSeconds(30)));
        Assert.Null(MobileCodeLimits.Problem(user, Now.AddMinutes(1)));

        for (var i = 1; i < MobileCodeLimits.PerDay; i++)
        {
            MobileCodeLimits.Sent(user, Now.AddMinutes(i * 2));
        }

        Assert.Equal(MobileCodeLimits.PerDay, user.MobileCodesSent);
        Assert.Contains("Try again tomorrow", MobileCodeLimits.Problem(user, Now.AddMinutes(30)));

        // A new day starts the count again.
        Assert.Null(MobileCodeLimits.Problem(user, Now.AddHours(4)));
        MobileCodeLimits.Sent(user, Now.AddHours(4));
        Assert.Equal(1, user.MobileCodesSent);
    }
}
