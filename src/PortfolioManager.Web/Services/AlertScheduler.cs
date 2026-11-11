using PortfolioManager.Core.Alerts;
using PortfolioManager.Core.Email;

namespace PortfolioManager.Web.Services;

/// <summary>
/// Sends the email alerts people turned on (Alerts, account menu): price targets reached, dividends announced,
/// Navexa sync failures and bank lines to allocate. Checks every 30 minutes while the site is running. Emails go out
/// when email is set up and Site:BaseUrl is set (emails need a link back to the site); the same alerts go to the phone
/// app as notifications when push is set up (Push settings) and the login has signed the app in on a phone.
/// </summary>
public sealed class AlertScheduler(IServiceScopeFactory scopes, IEmailSender email, IPushSender push, SiteBranding site,
    ILogger<AlertScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!EmailOn && !push.IsConfigured)
        {
            logger.LogWarning("Alerts are off: they need the Email settings and Site:BaseUrl, or the Push settings");
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

    private bool EmailOn => email.IsConfigured && !string.IsNullOrWhiteSpace(site.BaseUrl);

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

        var link = (site.BaseUrl ?? "").TrimEnd('/') + "/";
        var devices = scope.ServiceProvider.GetRequiredService<PushDeviceService>();
        foreach (var d in due)
        {
            try
            {
                var delivered = d.Items.Count == 0;
                if (d.Items.Count > 0 && EmailOn)
                {
                    try
                    {
                        await email.SendAsync(AlertKinds.Email(d, site.Name, link), ct);
                        delivered = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Still try the phones; if neither works the alerts stay waiting for next time
                        logger.LogError(ex, "Couldn't email alerts to login {LoginId} for portfolio {PortfolioId}", d.LoginId, d.PortfolioId);
                    }
                }
                if (d.Items.Count > 0 && push.IsConfigured && await PushAsync(devices, d, ct))
                    delivered = true;
                // With no email and no phone that took it, leave the alerts waiting for a phone to sign in
                if (delivered)
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

    /// <summary>Sends the digest to the login's phones; true when at least one phone took it.</summary>
    private async Task<bool> PushAsync(PushDeviceService devices, AlertDigest d, CancellationToken ct)
    {
        try
        {
            var tokens = (await devices.ListAsync(d.LoginId)).Select(x => x.Token).ToList();
            if (tokens.Count == 0)
                return false;
            var result = await push.SendAsync(tokens, PushRules.ForDigest(d), ct);
            await devices.RemoveTokensAsync(result.Gone);
            return result.Delivered > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A push failure (or the device table not made yet) mustn't stop the email or resend it next time
            logger.LogWarning(ex, "Couldn't send phone alerts to login {LoginId}", d.LoginId);
            return false;
        }
    }
}
