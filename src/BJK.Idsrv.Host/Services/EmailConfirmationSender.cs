using BJK.Modules.Email;
using BJK.Modules.Email.Contracts;
using BJK.Modules.Email.Templates;
using Microsoft.Extensions.Options;

namespace BJK.Idsrv.Host.Services;

/// <summary>Sends the "confirm your email address" email after registration (or when the user asks for another).</summary>
public interface IEmailConfirmationSender
{
    Task SendAsync(string email, string userId, string code, CancellationToken cancellationToken);
}

/// <summary>Links to this server's confirmation page, /Account/ConfirmEmail.</summary>
internal sealed class EmailConfirmationSender(
    IEmailSender emailSender,
    IOptions<EmailLinkOptions> links,
    IOptions<EmailOptions> emailOptions,
    IHttpContextAccessor http)
    : IEmailConfirmationSender
{
    public Task SendAsync(string email, string userId, string code, CancellationToken cancellationToken)
    {
        var origin = links.Value.PublicOrigin?.ToString().TrimEnd('/')
            ?? (http.HttpContext is { } context ? $"{context.Request.Scheme}://{context.Request.Host}" : null)
            ?? throw new InvalidOperationException("Set Email:PublicOrigin so confirmation links can be built.");
        var link = $"{origin}/Account/ConfirmEmail?userId={Uri.EscapeDataString(userId)}&code={Uri.EscapeDataString(code)}";

        var message = EmailTemplates.ConfirmEmail(link).To(email, emailOptions.Value.LogoUrl);

        return emailSender.SendAsync(message, cancellationToken);
    }
}
