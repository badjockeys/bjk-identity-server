using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace BJK.Idsrv.Host.Services.Support;

/// <summary>
/// Section <c>Support</c>: where people ask for help, e.g. after a failed age check. A mailto link for now; a
/// ticketing system (e.g. Zammad, Freshdesk or Help Scout) replaces it once email passes about 50 tickets a week.
/// </summary>
public sealed class SupportOptions
{
    public const string SectionName = "Support";

    /// <summary>The support address. Default support@badjockeys.com.</summary>
    public string Email { get; set; } = "support@badjockeys.com";
}

internal sealed class SupportOptionsValidator : IValidateOptions<SupportOptions>
{
    public ValidateOptionsResult Validate(string? name, SupportOptions options) =>
        MailAddress.TryCreate(options.Email, out var address) && address.Address == options.Email.Trim()
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Support:Email must be a plain email address, e.g. support@badjockeys.com.");
}

/// <summary>"Contact support" links.</summary>
public static class SupportLink
{
    /// <summary>
    /// A mailto link for help with an age check: subject "Age check help – {reference}", and a body naming the
    /// account's email (the handle lives in the core profile, so the person adds it).
    /// </summary>
    /// <param name="supportEmail">The support address.</param>
    /// <param name="reference">The attempt's reference, e.g. AGE-7KQ2MX.</param>
    /// <param name="accountEmail">The signed-in account's email, if known.</param>
    public static string ForAgeCheck(string supportEmail, string reference, string? accountEmail)
    {
        var subject = $"Age check help – {reference}";
        var body =
            $"Reference: {reference}\n"
            + $"Account email: {accountEmail ?? "(the email I sign in with)"}\n"
            + "My @handle: \n\n"
            + "What happened:\n";
        return $"mailto:{supportEmail}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";
    }
}

internal static class SupportServiceCollectionExtensions
{
    public static IServiceCollection AddSupport(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SupportOptions>()
            .Bind(configuration.GetSection(SupportOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SupportOptions>, SupportOptionsValidator>();
        return services;
    }
}
