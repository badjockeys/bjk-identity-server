using System.ComponentModel.DataAnnotations;
using BJK.Idsrv.Host.Data;
using BJK.Idsrv.Host.Services;
using BJK.Modules.Shared.Delivery;
using BJK.Modules.SMS.Contracts;
using BJK.Modules.SMS.Templates;
using BJK.Modules.User.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BJK.Idsrv.Host.Pages.Manage;

/// <summary>
/// The optional mobile number (UI overhaul step 6): add or change it (confirmed by a texted code), remove it, and choose
/// whether people with your number can find you in the mobile app. Never shown to anyone else.
/// </summary>
public sealed class MobileNumberModel(
    UserManager<BjkIdentityUser> users,
    ISMSSender sms,
    IAccountAgeLookup ages,
    TimeProvider clock,
    ILogger<MobileNumberModel> logger) : PageModel
{
    private const string PendingKey = "PendingMobile";

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>The confirmed number, masked, or null.</summary>
    public string? Current { get; private set; }

    public bool Discoverable { get; private set; }

    /// <summary>Under 18 (or age unknown): never findable by number.</summary>
    public bool IsMinor { get; private set; }

    /// <summary>A number waiting for its code, masked, or null.</summary>
    public string? Pending { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostSendCodeAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var number = MobileNumbers.Normalise(Input.Number);
        if (number is null)
        {
            ModelState.AddModelError("Input.Number", "Enter a UK mobile number, for example 07700 900123.");
            await LoadAsync(user);
            return Page();
        }

        if (user.PhoneNumberConfirmed && user.PhoneNumber == number)
        {
            TempData["Notice"] = "That's already your mobile number.";
            return RedirectToPage();
        }

        if (await users.Users.AnyAsync(u => u.PhoneNumber == number && u.PhoneNumberConfirmed && u.Id != user.Id, HttpContext.RequestAborted))
        {
            ModelState.AddModelError("Input.Number", "That number is on another Bad Jockeys account. Remove it there first.");
            await LoadAsync(user);
            return Page();
        }

        var now = clock.GetUtcNow();
        if (MobileCodeLimits.Problem(user, now) is { } problem)
        {
            ModelState.AddModelError(string.Empty, problem);
            await LoadAsync(user);
            return Page();
        }

        var code = await users.GenerateChangePhoneNumberTokenAsync(user, number);
        try
        {
            await sms.SendAsync(SMSTemplates.ConfirmMobileCode(number, code), HttpContext.RequestAborted);
        }
        catch (MessageDeliveryException ex)
        {
            // Never log the number.
            logger.LogWarning(ex, "A mobile confirmation code couldn't be sent.");
            ModelState.AddModelError(string.Empty, "We couldn't send a text just now. Please try again in a few minutes.");
            await LoadAsync(user);
            return Page();
        }

        MobileCodeLimits.Sent(user, now);
        await users.UpdateAsync(user);
        TempData[PendingKey] = number;
        TempData["Notice"] = $"We've texted a code to {MobileNumbers.Mask(number)}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostConfirmAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        if (TempData.Peek(PendingKey) is not string number)
        {
            TempData["Notice"] = "That code has expired. Ask for a new one.";
            return RedirectToPage();
        }

        var code = (Input.Code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal);
        var result = await users.ChangePhoneNumberAsync(user, number, code);
        if (!result.Succeeded)
        {
            ModelState.AddModelError("Input.Code", "That code isn't right, or it has expired. Check the text, or ask for a new code.");
            await LoadAsync(user);
            return Page();
        }

        TempData.Remove(PendingKey);
        user.MobileDiscoverable = !await IsMinorAsync(user);
        await users.UpdateAsync(user);
        TempData["Notice"] = "Your mobile number is confirmed.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await users.SetPhoneNumberAsync(user, null);
        user.MobileDiscoverable = false;
        await users.UpdateAsync(user);
        TempData.Remove(PendingKey);
        TempData["Notice"] = "Your mobile number is removed.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDiscoverableAsync(bool on)
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        if (on && (!user.PhoneNumberConfirmed || await IsMinorAsync(user)))
        {
            TempData["Notice"] = user.PhoneNumberConfirmed
                ? "People under 18 can't be found by their number."
                : "Confirm a mobile number first.";
            return RedirectToPage();
        }

        user.MobileDiscoverable = on;
        await users.UpdateAsync(user);
        TempData["Notice"] = on ? "People with your number can now find you." : "People can no longer find you by your number.";
        return RedirectToPage();
    }

    private async Task LoadAsync(BjkIdentityUser user)
    {
        Current = user is { PhoneNumberConfirmed: true, PhoneNumber: { } number } ? MobileNumbers.Mask(number) : null;
        Discoverable = user.MobileDiscoverable && Current is not null;
        IsMinor = await IsMinorAsync(user);
        Pending = TempData.Peek(PendingKey) is string pending ? MobileNumbers.Mask(pending) : null;
    }

    private async Task<bool> IsMinorAsync(BjkIdentityUser user)
    {
        var facts = Guid.TryParse(user.Id, out var id) ? await ages.GetAsync(id, HttpContext.RequestAborted) : null;
        return facts is null || facts.DateOfBirth is null || facts.IsMinorOn(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
    }

    public sealed class InputModel
    {
        [Display(Name = "Mobile number")]
        public string? Number { get; set; }

        [Display(Name = "Code")]
        public string? Code { get; set; }
    }
}
