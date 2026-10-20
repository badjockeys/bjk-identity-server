using System.Text;
using BJK.Idsrv.Host.Data;

namespace BJK.Idsrv.Host.Services;

/// <summary>
/// UK mobile numbers, for the optional mobile number on an account (UI overhaul step 6). Stored in E.164
/// (<c>+447700900123</c>), never shown to anyone else, and never logged.
/// </summary>
public static class MobileNumbers
{
    /// <summary>
    /// The number in E.164, or null if it isn't a UK mobile number. Accepts the usual ways of writing one:
    /// <c>07700 900123</c>, <c>+44 7700 900123</c>, <c>447700900123</c>, <c>0044 7700 900123</c>, with spaces, dashes,
    /// dots or brackets.
    /// </summary>
    public static string? Normalise(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var digits = new StringBuilder();
        var plus = false;
        foreach (var c in input.Trim())
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
            else if (c == '+' && digits.Length == 0 && !plus)
            {
                plus = true;
            }
            else if (c is not (' ' or '-' or '.' or '(' or ')'))
            {
                return null;
            }
        }

        var d = digits.ToString();
        var national = plus && d.StartsWith("44", StringComparison.Ordinal) ? d[2..]
            : !plus && d.StartsWith("0044", StringComparison.Ordinal) ? d[4..]
            : !plus && d.StartsWith("44", StringComparison.Ordinal) && d.Length == 12 ? d[2..]
            : !plus && d.StartsWith('0') ? d[1..]
            : null;

        // A UK mobile: 7 then 9 more digits (07xxx xxxxxx), not 070 (personal numbers) or 076 (pagers) except 07624 (Isle of Man).
        if (national is not { Length: 10 } || national[0] != '7' || national.StartsWith("70", StringComparison.Ordinal)
            || (national.StartsWith("76", StringComparison.Ordinal) && !national.StartsWith("7624", StringComparison.Ordinal)))
        {
            return null;
        }

        return "+44" + national;
    }

    /// <summary>"07700 *** 123": enough for the owner to recognise it, for the account page.</summary>
    public static string Mask(string e164) =>
        e164.StartsWith("+44", StringComparison.Ordinal) && e164.Length == 13
            ? $"0{e164[3..7]} *** {e164[^3..]}"
            : "your number";
}

/// <summary>
/// How often confirmation codes can be texted to an account: one a minute, five a day (UTC), so a stolen session can't
/// run up texts and nobody is flooded.
/// </summary>
public static class MobileCodeLimits
{
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(1);

    public const int PerDay = 5;

    /// <summary>Null if a code can be sent now, otherwise why not.</summary>
    public static string? Problem(BjkIdentityUser user, DateTimeOffset now)
    {
        if (user.MobileCodeSentAt is { } sent && now - sent < Gap)
        {
            return "We've just sent a code. Wait a minute before asking for another.";
        }

        return user.MobileCodesDay == DateOnly.FromDateTime(now.UtcDateTime) && user.MobileCodesSent >= PerDay
            ? $"You've asked for {PerDay} codes today. Try again tomorrow."
            : null;
    }

    /// <summary>Records that a code was sent now.</summary>
    public static void Sent(BjkIdentityUser user, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        user.MobileCodesSent = user.MobileCodesDay == today ? user.MobileCodesSent + 1 : 1;
        user.MobileCodesDay = today;
        user.MobileCodeSentAt = now;
    }
}
