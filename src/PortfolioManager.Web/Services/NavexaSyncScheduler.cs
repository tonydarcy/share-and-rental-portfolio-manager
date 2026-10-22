using PortfolioManager.Core.NavexaSync;

namespace PortfolioManager.Web.Services;

/// <summary>
/// Runs the daily or weekly Navexa syncs people have turned on (Import, Navexa sync). It looks for due
/// portfolios every 30 minutes while the site is running; if IIS has put the site to sleep, a due sync
/// runs soon after it wakes up. Each sync only adds records that are ready, and never the same one twice.
/// </summary>
public sealed class NavexaSyncScheduler(IServiceScopeFactory scopes, ILogger<NavexaSyncScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstCheck, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await RunDueAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunDueAsync(CancellationToken ct)
    {
        IReadOnlyList<int> due;
        try
        {
            using var scope = scopes.CreateScope();
            due = await scope.ServiceProvider.GetRequiredService<NavexaSyncService>().GetDueAsync(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // Usually the database migration hasn't been run yet
            logger.LogWarning(ex, "Couldn't look for due Navexa syncs");
            return;
        }

        foreach (var portfolioId in due)
        {
            using var scope = scopes.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<NavexaSyncService>();
            try
            {
                var result = await sync.SyncAsync(portfolioId, ct);
                logger.LogInformation("Scheduled Navexa sync for portfolio {PortfolioId}: {Summary}", portfolioId, result.Summary);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled Navexa sync for portfolio {PortfolioId} failed", portfolioId);
                var message = ex is NavexaApiException ? ex.Message : "something went wrong on this site. Try Sync now to see the error.";
                try
                {
                    await sync.RecordFailureAsync(portfolioId, message);
                }
                catch (Exception inner)
                {
                    logger.LogError(inner, "Couldn't record the failed Navexa sync for portfolio {PortfolioId}", portfolioId);
                }
            }
        }
    }
}
