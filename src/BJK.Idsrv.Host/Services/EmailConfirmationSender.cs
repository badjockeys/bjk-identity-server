using BJK.Idsrv.Host.Services.Email;
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
    IOptions<EmailOptions> options,
    IHttpContextAccessor http)
    : IEmailConfirmationSender
{
    public Task SendAsync(string email, string userId, string code, CancellationToken cancellationToken)
    {
        var origin = options.Value.PublicOrigin?.ToString().TrimEnd('/')
            ?? (http.HttpContext is { } context ? $"{context.Request.Scheme}://{context.Request.Host}" : null)
            ?? throw new InvalidOperationException("Set Email:PublicOrigin so confirmation links can be built.");
        var link = $"{origin}/Account/ConfirmEmail?userId={Uri.EscapeDataString(userId)}&code={Uri.EscapeDataString(code)}";

        var body = string.Join(
            "\n",
            "Welcome to Bad Jockeys!",
            string.Empty,
            $"Confirm your email address: {link}",
            string.Empty,
            "You need to confirm it before you can post or comment.",
            "If you didn't create an account, you can ignore this email.");

        return emailSender.SendAsync(new EmailMessage(email, "Confirm your Bad Jockeys email address", body), cancellationToken);
    }
}
