namespace BJK.Idsrv.Host.Services;

/// <summary>Delivers password reset tokens to users. Will be replaced by an email sender.</summary>
public interface IPasswordResetSender
{
    Task SendAsync(string email, string token, CancellationToken cancellationToken);
}

/// <summary>
/// Until email sending exists: in Development, writes the reset token to the log so the flow can be
/// tested end to end. In any other environment the token is never logged.
/// </summary>
internal sealed class LoggingPasswordResetSender(IHostEnvironment environment, ILogger<LoggingPasswordResetSender> logger)
    : IPasswordResetSender
{
    public Task SendAsync(string email, string token, CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment())
        {
            logger.LogWarning("DEVELOPMENT ONLY - password reset token for {Email}: {Token}", email, token);
        }
        else
        {
            logger.LogError("A password reset was requested but no email sender is configured, so the user wasn't sent a token.");
        }

        return Task.CompletedTask;
    }
}
