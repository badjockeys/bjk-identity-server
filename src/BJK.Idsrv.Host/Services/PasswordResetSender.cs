using BJK.Modules.Email;
using BJK.Modules.Email.Contracts;
using BJK.Modules.Email.Templates;
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
    IOptions<EmailOptions> emailOptions)
    : IPasswordResetSender
{
    public Task SendAsync(string email, string token, CancellationToken cancellationToken)
    {
        var resetUrl = links.Value.PasswordResetUrl;
        var link = resetUrl is null
            ? null
            : $"{resetUrl}?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
        var message = EmailTemplates.PasswordReset(token, link).To(email, emailOptions.Value.LogoUrl);

        return emailSender.SendAsync(message, cancellationToken);
    }
}
