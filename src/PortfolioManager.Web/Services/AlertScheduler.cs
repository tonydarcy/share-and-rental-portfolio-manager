using PortfolioManager.Core.Alerts;
using PortfolioManager.Core.Email;

namespace PortfolioManager.Web.Services;

/// <summary>
/// Sends the email alerts people turned on (Alerts, account menu): price targets reached, dividends announced,
/// Navexa sync failures and bank lines to allocate. Checks every 30 minutes while the site is running, and only
/// when email is set up and Site:BaseUrl is set (emails need a link back to the site).
/// </summary>
public sealed class AlertScheduler(IServiceScopeFactory scopes, IEmailSender email, SiteBranding site, ILogger<AlertScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!email.IsConfigured || string.IsNullOrWhiteSpace(site.BaseUrl))
        {
            logger.LogWarning("Email alerts are off: they need the Email settings and Site:BaseUrl");
            return;
        }
        try
        {
            await Task.Delay(FirstCheck, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await RunAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();
        var now = DateTime.UtcNow;
        IReadOnlyList<AlertDigest> due;
        try
        {
            due = await alerts.GetDueAsync(now);
        }
        catch (Exception ex)
        {
            // Usually the database migration hasn't been run yet
            logger.LogWarning(ex, "Couldn't look for email alerts");
            return;
        }

        var link = site.BaseUrl!.TrimEnd('/') + "/";
        foreach (var d in due)
        {
            try
            {
                if (d.Items.Count > 0)
                    await email.SendAsync(AlertKinds.Email(d, site.Name, link), ct);
                await alerts.MarkCheckedAsync(d, now);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Not marked, so it's tried again next time
                logger.LogError(ex, "Couldn't send alerts to login {LoginId} for portfolio {PortfolioId}", d.LoginId, d.PortfolioId);
            }
        }
    }
}
