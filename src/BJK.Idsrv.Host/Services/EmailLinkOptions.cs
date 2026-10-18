namespace BJK.Idsrv.Host.Services;

/// <summary>
/// Addresses used in the links this server emails. Section <c>Email</c>, shared with the Email module's
/// sending settings (provider, sender, SendGrid key).
/// </summary>
public sealed class EmailLinkOptions
{
    public const string SectionName = "Email";

    /// <summary>
    /// Page where users choose a new password: this server's /Account/ResetPassword, e.g. https://auth.badjockeys.com/Account/ResetPassword.
    /// The email links to it with <c>?email=…&amp;token=…</c>. If not set, the email only contains the reset code.
    /// </summary>
    public Uri? PasswordResetUrl { get; set; }

    /// <summary>
    /// This identity server's public address, e.g. https://auth.badjockeys.com, for links in emails (confirm email).
    /// If not set, the address of the current request is used.
    /// </summary>
    public Uri? PublicOrigin { get; set; }
}
