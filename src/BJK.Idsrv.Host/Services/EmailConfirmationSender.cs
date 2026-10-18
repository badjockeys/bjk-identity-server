using BJK.Modules.Communications;
using BJK.Modules.Communications.Contracts;
using BJK.Modules.Communications.Email;
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
    IOptions<EmailOptions> email,
    IHttpContextAccessor http)
    : IEmailConfirmationSender
{
    public Task SendAsync(string email, string userId, string code, CancellationToken cancellationToken)
    {
        var origin = links.Value.PublicOrigin?.ToString().TrimEnd('/')
            ?? (http.HttpContext is { } context ? $"{context.Request.Scheme}://{context.Request.Host}" : null)
            ?? throw new InvalidOperationException("Set Email:PublicOrigin so confirmation links can be built.");
        var link = $"{origin}/Account/ConfirmEmail?userId={Uri.EscapeDataString(userId)}&code={Uri.EscapeDataString(code)}";

        var message = BrandedEmail.Create("Confirm your email address")
            .Paragraph("Welcome to Bad Jockeys!")
            .Button("Confirm email address", link)
            .Paragraph("You need to confirm it before you can post or comment.")
            .Note("If you didn't create an account, you can ignore this email.")
            .ToMessage(email, "Confirm your Bad Jockeys email address", this.email.Value.LogoUrl);

        return emailSender.SendAsync(message, cancellationToken);
    }
}
