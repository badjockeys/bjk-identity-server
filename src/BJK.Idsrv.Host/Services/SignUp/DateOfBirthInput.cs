using BJK.Modules.User.Domain;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BJK.Idsrv.Host.Services.SignUp;

/// <summary>
/// The day, month and year boxes of a date of birth question (the GOV.UK date input pattern: three labelled number
/// boxes in a fieldset, which works with screen readers and phone keypads). Shared by sign-up, a first social
/// sign-in and older accounts asked for it at sign-in.
/// </summary>
public sealed class DateOfBirthInput
{
    /// <summary>Day, 1 to 31.</summary>
    public int? Day { get; set; }

    /// <summary>Month, 1 to 12.</summary>
    public int? Month { get; set; }

    /// <summary>Four-digit year.</summary>
    public int? Year { get; set; }

    /// <summary>The date, if the three boxes make a real date.</summary>
    public DateOnly? Value =>
        Day is { } day && Month is { } month && Year is { } year
        && year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;

    /// <summary>
    /// Checks the boxes: all filled, a real date, not in the future and not over 120 years ago. Adds an error under
    /// <paramref name="key"/> otherwise. Doesn't check the minimum age: callers send under-13s to the refusal page.
    /// </summary>
    /// <param name="modelState">Where errors go.</param>
    /// <param name="key">The field's model state key, e.g. "Input.DateOfBirth".</param>
    /// <param name="today">Today's date.</param>
    /// <returns>The date, or null if it isn't valid.</returns>
    public DateOnly? Validate(ModelStateDictionary modelState, string key, DateOnly today)
    {
        if (Day is null && Month is null && Year is null)
        {
            modelState.AddModelError(key, "Enter your date of birth.");
            return null;
        }

        if (Day is null || Month is null || Year is null)
        {
            modelState.AddModelError(key, "Your date of birth needs a day, a month and a year.");
            return null;
        }

        if (Year < 1000)
        {
            modelState.AddModelError(key, "Enter the year as four digits, for example 2007.");
            return null;
        }

        if (Value is not { } date)
        {
            modelState.AddModelError(key, "Your date of birth must be a real date.");
            return null;
        }

        if (!AgeRules.IsPlausible(date, today))
        {
            modelState.AddModelError(key, date > today ? "Your date of birth must be in the past." : "Please check the year you were born.");
            return null;
        }

        return date;
    }
}
