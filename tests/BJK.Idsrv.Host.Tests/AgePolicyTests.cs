using System.Security.Claims;
using BJK.Idsrv.Host.Services;
using BJK.Idsrv.Host.Services.SignUp;
using BJK.Modules.User.Contracts;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Xunit;

namespace BJK.Idsrv.Host.Tests;

public sealed class AgePolicyTests
{
    private static readonly DateOnly Today = new(2026, 10, 19);

    [Fact]
    public void Date_of_birth_boxes_make_a_date()
    {
        var input = new DateOfBirthInput { Day = 27, Month = 3, Year = 2010 };
        var state = new ModelStateDictionary();

        Assert.Equal(new DateOnly(2010, 3, 27), input.Validate(state, "dob", Today));
        Assert.True(state.IsValid);
    }

    [Theory]
    [InlineData(null, null, null, "Enter your date of birth")]
    [InlineData(27, null, 2010, "a day, a month and a year")]
    [InlineData(31, 2, 2010, "real date")]
    [InlineData(1, 1, 10, "four digits")]
    [InlineData(1, 1, 2030, "in the past")]
    [InlineData(1, 1, 1890, "check the year")]
    public void Date_of_birth_mistakes_get_a_clear_message(int? day, int? month, int? year, string expected)
    {
        var state = new ModelStateDictionary();

        var date = new DateOfBirthInput { Day = day, Month = month, Year = year }.Validate(state, "dob", Today);

        Assert.Null(date);
        Assert.Contains(expected, state["dob"]!.Errors.Single().ErrorMessage);
    }

    [Fact]
    public void A_15_year_old_gets_the_minor_claim_and_no_age_verified()
    {
        var claims = new List<Claim>();

        AgeClaims.AddTo(claims, new AccountAgeFacts(new DateOnly(2011, 3, 1), AgeVerified: false), Today);

        var claim = Assert.Single(claims);
        Assert.Equal("minor", claim.Type);
        Assert.Equal("true", claim.Value);
    }

    [Fact]
    public void An_age_verified_adult_gets_age_verified_and_no_minor_claim()
    {
        var claims = new List<Claim>();

        AgeClaims.AddTo(claims, new AccountAgeFacts(new DateOnly(1990, 6, 15), AgeVerified: true), Today);

        var claim = Assert.Single(claims);
        Assert.Equal("age_verified", claim.Type);
        Assert.Equal("true", claim.Value);
    }

    [Fact]
    public void The_minor_claim_lapses_on_the_18th_birthday()
    {
        var facts = new AccountAgeFacts(new DateOnly(2008, 10, 20), AgeVerified: false);
        var dayBefore = new List<Claim>();
        var birthday = new List<Claim>();

        AgeClaims.AddTo(dayBefore, facts, Today);
        AgeClaims.AddTo(birthday, facts, Today.AddDays(1));

        Assert.Contains(dayBefore, c => c.Type == "minor");
        Assert.Empty(birthday);
    }

    [Fact]
    public void No_account_record_adds_nothing_and_claims_are_not_duplicated()
    {
        var claims = new List<Claim> { new("age_verified", "true") };

        AgeClaims.AddTo(claims, null, Today);
        AgeClaims.AddTo(claims, new AccountAgeFacts(new DateOnly(1990, 1, 1), AgeVerified: true), Today);

        Assert.Single(claims);
    }
}
