using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Prices;

// Daily end-of-day price update, run by Windows Task Scheduler (see README-razor-pages.md).
// Exit codes: 0 all updated, 1 some symbols or trends failed, 2 not configured or could not run.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-AU");

// Settings are read from the exe's folder, whatever folder Task Scheduler starts it in
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
using var host = builder.Build();
var config = host.Services.GetRequiredService<IConfiguration>();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PriceUpdater");

try
{
    var connectionString = config.GetConnectionString("Default");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        logger.LogError("ConnectionStrings:Default is not set. Put it in appsettings.Production.json next to the exe or set ConnectionStrings__Default.");
        return 2;
    }

    var providerName = config["Prices:Provider"] ?? "Eodhd";
    if (!providerName.Equals("Eodhd", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogError("Unknown Prices:Provider '{Provider}'. Supported: Eodhd.", providerName);
        return 2;
    }

    var eodhd = config.GetSection("Prices:Eodhd").Get<EodhdOptions>() ?? new EodhdOptions();
    if (string.IsNullOrWhiteSpace(eodhd.ApiKey))
    {
        logger.LogError("Prices:Eodhd:ApiKey is not set. Put it in appsettings.Production.json next to the exe or set Prices__Eodhd__ApiKey.");
        return 2;
    }

    // --from yyyy-MM-dd fetches further back, e.g. to fill in history for a new holding's 200-day average
    var to = DateOnly.FromDateTime(DateTime.Now);
    var from = to.AddDays(-config.GetValue("Prices:LookbackDays", 10));
    if (config["from"] is { Length: > 0 } fromArg
        && (!DateOnly.TryParseExact(fromArg, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out from) || from > to))
    {
        logger.LogError("--from must be a past date written yyyy-MM-dd.");
        return 2;
    }

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    var service = new PriceUpdateService(
        // Prices:AlwaysUpdate lists symbols to fetch even when nobody holds them, e.g. "ASX:STW" for the performance comparison
        new PriceStore(new SqlConnectionFactory(connectionString), PriceStore.ParseSymbols(config.GetSection("Prices:AlwaysUpdate").Get<string[]>())),
        new EodhdPriceProvider(http, eodhd));

    var summary = await service.RunAsync(from, to, message => logger.LogInformation("{Message}", message));
    foreach (var error in summary.TrendErrors)
        logger.LogWarning("{Error}", error);

    // Announced dividends and splits for held stocks, offered on Corporate actions > Announced (two API calls per stock)
    var announcementsFailed = false;
    if (config.GetValue("Prices:Announcements:Enabled", true))
    {
        var announcementsFrom = to.AddDays(-config.GetValue("Prices:Announcements:LookbackDays", 60));
        if (config["from"] is { Length: > 0 } && from < announcementsFrom)
            announcementsFrom = from;
        var announcements = await new AnnouncementUpdateService(
            new AnnouncementStore(new SqlConnectionFactory(connectionString)),
            new EodhdPriceProvider(http, eodhd)).RunAsync(announcementsFrom, message => logger.LogInformation("{Message}", message));
        announcementsFailed = announcements.HasErrors;
    }
    // What is inside held ETFs, for the look-through on Reports > Diversity. Off unless the EODHD plan includes
    // fundamentals (each call counts as 10); each stock is asked at most once every 30 days.
    var lookThroughFailed = false;
    if (config.GetValue("Prices:LookThrough:Enabled", false))
    {
        var lookThrough = await new PortfolioManager.Core.Reports.FundExposureUpdateService(
            new SqlConnectionFactory(connectionString), new EodhdPriceProvider(http, eodhd))
            .RunAsync(to, message => logger.LogInformation("{Message}", message));
        lookThroughFailed = lookThrough.Errors.Count > 0;
    }
    return summary.HasErrors || announcementsFailed || lookThroughFailed ? 1 : 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "Price update failed.");
    return 2;
}
