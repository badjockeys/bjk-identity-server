namespace BJK.Idsrv.Host.Services.Email;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public const string LogProvider = "Log";
    public const string SendGridProvider = "SendGrid";

    /// <summary>
    /// "Log" (default): nothing is sent; in Development the whole email is written to the log.
    /// "SendGrid": planned, not implemented yet.
    /// </summary>
    public string Provider { get; set; } = LogProvider;

    public string? FromAddress { get; set; }

    public string FromName { get; set; } = "Bad Jockeys";

    /// <summary>
    /// Page in the web app where users choose a new password, e.g. https://app.example.com/reset-password.
    /// The email links to it with <c>?email=…&amp;token=…</c>. If not set, the email only contains the reset code.
    /// </summary>
    public Uri? PasswordResetUrl { get; set; }

    public SendGridOptions SendGrid { get; set; } = new();
}

public sealed class SendGridOptions
{
    /// <summary>Secret: user secrets locally, Key Vault in Azure. Never appsettings.</summary>
    public string? ApiKey { get; set; }
}
