using PortfolioManager.Core.Email;

namespace PortfolioManager.Web.Services;

/// <summary>
/// Used when the site has no Email settings: nothing is sent, and pages offer another way (for example the sign-up
/// link to copy). Messages are written to the log so a developer can still follow the links.
/// </summary>
public sealed class LogOnlyEmailSender(ILogger<LogOnlyEmailSender> logger) : IEmailSender
{
    public bool IsConfigured => false;

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        logger.LogInformation("Email not sent (no Email settings). To: {To}. Subject: {Subject}.\n{Text}", message.To, message.Subject, message.Text);
        return Task.CompletedTask;
    }
}
