using BJK.Modules.Communications;
using BJK.Modules.Communications.Contracts;
using BJK.Modules.Communications.Email;
using Microsoft.Extensions.Options;

namespace BJK.Idsrv.Host.Services;

/// <summary>Delivers password reset tokens to users.</summary>
public interface IPasswordResetSender
{
    Task SendAsync(string email, string token, CancellationToken cancellationToken);
}

/// <summary>Emails the reset token as a link to the reset page (if configured) and as a code.</summary>
internal sealed class EmailPasswordResetSender(
    IEmailSender emailSender,
    IOptions<EmailLinkOptions> links,
    IOptions<EmailOptions> email)
    : IPasswordResetSender
{
    public Task SendAsync(string email, string token, CancellationToken cancellationToken)
    {
        var resetUrl = links.Value.PasswordResetUrl;

        var builder = BrandedEmail.Create("Reset your password")
            .Paragraph("We received a request to reset the password for your Bad Jockeys account.");
        if (resetUrl is not null)
        {
            builder.Button("Reset your password", $"{resetUrl}?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}");
        }

        var message = builder
            .Code("Password reset code", token)
            .Note("If you didn't ask for this, you can ignore this email. Your password won't change.")
            .ToMessage(email, "Reset your Bad Jockeys password", this.email.Value.LogoUrl);

        return emailSender.SendAsync(message, cancellationToken);
    }
}
