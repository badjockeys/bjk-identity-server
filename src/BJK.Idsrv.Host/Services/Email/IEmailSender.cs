namespace BJK.Idsrv.Host.Services.Email;

public sealed record EmailMessage(string To, string Subject, string TextBody);

/// <summary>Sends email. Which implementation is used is chosen by <c>Email:Provider</c>.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
