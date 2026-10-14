using BJK.Idsrv.Host.Services.Email;
using Microsoft.Extensions.Options;

namespace BJK.Idsrv.Host.Services;

/// <summary>Delivers password reset tokens to users.</summary>
public interface IPasswordResetSender
{
    Task SendAsync(string email, string token, CancellationToken cancellationToken);
}

/// <summary>Emails the reset token as a link to the web app's reset page (if configured) and as a code.</summary>
internal sealed class EmailPasswordResetSender(IEmailSender emailSender, IOptions<EmailOptions> options)
    : IPasswordResetSender
{
    public Task SendAsync(string email, string token, CancellationToken cancellationToken)
    {
        var resetUrl = options.Value.PasswordResetUrl;
        var link = resetUrl is null
            ? null
            : $"{resetUrl}?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";

        var body = string.Join(
            "\n",
            "We received a request to reset the password for your Bad Jockeys account.",
            string.Empty,
            link is null ? string.Empty : $"Reset your password: {link}",
            $"Password reset code: {token}",
            string.Empty,
            "If you didn't ask for this, you can ignore this email. Your password won't change.");

        return emailSender.SendAsync(
            new EmailMessage(email, "Reset your Bad Jockeys password", body),
            cancellationToken);
    }
}
