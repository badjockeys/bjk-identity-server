namespace BJK.Idsrv.Host.Services.Email;

public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .Validate(
                o => o.Provider is EmailOptions.LogProvider or EmailOptions.SendGridProvider,
                $"Email:Provider must be '{EmailOptions.LogProvider}' or '{EmailOptions.SendGridProvider}'.")
            .ValidateOnStart();

        var provider = configuration[$"{EmailOptions.SectionName}:{nameof(EmailOptions.Provider)}"] ?? EmailOptions.LogProvider;

        if (string.Equals(provider, EmailOptions.SendGridProvider, StringComparison.Ordinal))
        {
            // Placeholder: add a SendGridEmailSender here (from address, API key from Key Vault).
            throw new InvalidOperationException(
                "Email:Provider is 'SendGrid', but SendGrid sending isn't implemented yet. Use 'Log' for now.");
        }

        services.AddSingleton<IEmailSender, LoggingEmailSender>();
        return services;
    }
}
