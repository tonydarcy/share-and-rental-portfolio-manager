using PortfolioManager.Core.Alerts;
using PortfolioManager.Core.Dashboard;
using PortfolioManager.Core.Holdings;
using PortfolioManager.Core.Performance;
using PortfolioManager.Core.Practice;
using PortfolioManager.Core.Prices;

namespace PortfolioManager.Web.Api;

/// <summary>
/// The tools the phone app needs beyond the site's records: the dashboard's headline figures, a share's price history and
/// code search, file attachments (a photo of a receipt or contract note), and the phone's registration for alerts.
/// </summary>
public sealed partial class AgentTools
{
    public const int UpcomingPayoutDays = 90, MaxUpcomingPayouts = 10, DefaultHistoryDays = 365;

    private static List<AgentTool> BuildMobile() =>
    [
        new("get_dashboard", "Dashboard summary",
            "The dashboard's headline figures: total value (shares and cash), the latest day's change, open holdings' value, cost "
            + "and gain, the biggest holdings with their share of the total, and payouts expected in the next 90 days.",
            false, new ToolSchema().Portfolio().Build(),
            Dashboard),

        new("get_price_history", "Price history",
            "Daily prices on file for one share, oldest first, for a chart: close, and open, high, low and volume where known. "
            + "Also its name, sector, units this portfolio holds, the latest change and the year's range.",
            false, new ToolSchema().Portfolio()
                .String("exchange", "The exchange, e.g. ASX", required: true)
                .String("stockCode", "The share's code, e.g. CBA", required: true)
                .Date("from", "First day (default: a year before today)").Build(),
            PriceHistory),

        new("search_securities", "Find a share code",
            "Shares whose code starts with, or whose name contains, the text, best matches first (up to 20). Use it to pick the "
            + "exchange and code for add_trade.",
            false, new ToolSchema().Portfolio()
                .String("query", "Part of the code or name, e.g. CBA or Commonwealth", required: true)
                .String("exchange", "Only this exchange, e.g. ASX").Build(),
            SearchSecurities),

        new("list_attachments", "List attached files",
            "Files attached to the portfolio's records (name, type, size, note, who uploaded it), newest first. Give kind and "
            + "targetId to see one record's files.",
            false, new ToolSchema().Portfolio()
                .String("kind", "Only files for this kind of record", choices: [.. AttachmentKinds.All])
                .Int("targetId", "Only files for this record (with kind)").Build(),
            ListAttachments),

        new("add_attachment", "Attach a file",
            "Attaches a file, such as a photo of a receipt or contract note, to a record: a trade (tradeId from list_trades), "
            + "bill, journal, expense claim, rental property, rental job or inspection. Send the file as base64. Up to 10 MB; "
            + "PDF, pictures (JPG, PNG, HEIC and the like), and office or text files.",
            true, new ToolSchema().Portfolio()
                .String("kind", "The kind of record", required: true, choices: [.. AttachmentKinds.All.Where(AttachmentKinds.IsRecord)])
                .Int("targetId", "The record's id", required: true)
                .String("fileName", "The file's name with its extension, e.g. receipt.jpg", required: true)
                .String("contentBase64", "The file's bytes, base64 encoded", required: true)
                .String("note", "Note to keep with the file").Build(),
            AddAttachment),

        new("register_push_device", "Turn on phone alerts",
            "Registers this phone for alert notifications (the same alerts as the email ones, chosen on the Alerts page). "
            + "Send the Firebase Cloud Messaging registration token the app was given; call again whenever it changes.",
            false, new ToolSchema()
                .String("platform", "The phone's system", required: true, choices: [.. PushRules.Platforms])
                .String("token", "The Firebase registration token", required: true)
                .String("deviceName", "A name to show in the list of phones, e.g. Tony's Pixel").Build(),
            RegisterDevice, NeedsPortfolio: false),

        new("list_push_devices", "List phones with alerts",
            "The phones signed in to the app that get alert notifications for this login.",
            false, new ToolSchema().Build(),
            ListDevices, NeedsPortfolio: false),

        new("unregister_push_device", "Turn off phone alerts",
            "Stops alert notifications to a phone, by its token (on signing out of the app) or its id from list_push_devices.",
            false, new ToolSchema()
                .String("token", "The phone's Firebase registration token")
                .Int("pushDeviceId", "The phone, from list_push_devices").Build(),
            UnregisterDevice, NeedsPortfolio: false),
    ];

    private static async Task<object?> Dashboard(AgentCall c)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var value = await c.Service<DashboardService>().GetValueAsync(c.PortfolioId);
        var holdings = await c.Service<HoldingsService>().GetHoldingsAsync(c.PortfolioId, null, true);
        var perf = await c.Service<PerformanceService>().GetAsync(c.PortfolioId, today.AddDays(-14), today);
        var projected = await c.Service<DashboardService>().GetProjectedPayoutsAsync(c.PortfolioId);
        return new
        {
            Currency = value.PortfolioCurrency,
            value.TotalPortfolioValue,
            value.TotalShareValue,
            value.TotalCashValue,
            LatestMove = DashboardFigures.LatestMove(perf.Days),
            Holdings = holdings.Total,
            TopHoldings = DashboardFigures.Top(holdings.Holdings).Select(t => new
            {
                t.Holding.ExchangeCode, t.Holding.StockCode, t.Holding.Units, t.Holding.Price, t.Holding.MarketValue,
                t.Holding.CapitalGain, t.GainPercent, t.Weight,
            }),
            UpcomingPayouts = UpcomingPayouts(projected, today),
        };
    }

    /// <summary>Payouts expected from today to 90 days on, soonest first, at most 10.</summary>
    public static IReadOnlyList<ProjectedPayout> UpcomingPayouts(IEnumerable<ProjectedPayout> projected, DateOnly today) =>
        projected.Where(p => DateOnly.FromDateTime(p.ProjectedPaymentDate) >= today
                             && DateOnly.FromDateTime(p.ProjectedPaymentDate) <= today.AddDays(UpcomingPayoutDays))
            .OrderBy(p => p.ProjectedPaymentDate).Take(MaxUpcomingPayouts).ToList();

    private static async Task<object?> PriceHistory(AgentCall c)
    {
        var exchange = Required(c, "exchange").ToUpperInvariant();
        var code = Required(c, "stockCode").ToUpperInvariant();
        var from = c.Date("from") ?? DateOnly.FromDateTime(DateTime.Today).AddDays(-DefaultHistoryDays);
        var quote = await c.Service<QuoteService>().GetQuoteAsync(c.PortfolioId, exchange, code);
        var range = quote.YearRange;
        return new
        {
            quote.Exchange, quote.Code, quote.Name, quote.Sector, quote.UnitsHeld,
            Latest = quote.Latest?.Close, LatestDate = quote.Latest?.Date, quote.Change, quote.ChangePct,
            YearLow = range?.Low, YearHigh = range?.High,
            Prices = quote.Bars.Where(b => b.Date >= from).Select(b => new { b.Date, b.Close, b.Open, b.High, b.Low, b.Volume }),
        };
    }

    private static async Task<object?> SearchSecurities(AgentCall c) =>
        await c.Service<QuoteService>().SearchAsync(Required(c, "query"), c.String("exchange"));

    private static async Task<object?> ListAttachments(AgentCall c)
    {
        var kind = c.String("kind");
        if (kind is not null && !AttachmentKinds.All.Contains(kind))
            throw new AgentToolException(400, "kind must be one of: " + string.Join(", ", AttachmentKinds.All) + ".");
        return (await c.Service<AttachmentService>().ListAsync(c.PortfolioId, kind, kind is null ? null : c.Int("targetId")))
            .Select(a => new { AttachmentId = a.Id, a.Kind, a.TargetId, a.Year, a.FileName, a.ContentType, a.Size, a.Note, a.UploadedBy, a.Uploaded })
            .ToList();
    }

    private static async Task<object?> AddAttachment(AgentCall c)
    {
        var kind = Required(c, "kind");
        if (!AttachmentKinds.IsRecord(kind))
            throw new AgentToolException(400, "kind must be one of: " + string.Join(", ", AttachmentKinds.All.Where(AttachmentKinds.IsRecord)) + ".");
        var data = DecodeBase64(Required(c, "contentBase64"));
        var saved = await c.Service<AttachmentService>().AddAsync(c.PortfolioId, c.Caller.UserId, kind, c.RequiredInt("targetId"), null,
            Required(c, "fileName"), data, c.String("note"));
        if (!saved.Ok)
            throw new AgentToolException(400, saved.Error!);
        return new { AttachmentId = saved.Id, Size = data.Length };
    }

    /// <summary>The file's bytes from base64 (a data: URL prefix is allowed), or a 400 when it isn't base64 or is too big.</summary>
    public static byte[] DecodeBase64(string text)
    {
        var comma = text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? text.IndexOf(',') : -1;
        var body = comma >= 0 ? text[(comma + 1)..] : text;
        // Base64 grows a file by a third, so anything this long is over the limit anyway
        if (body.Length > AttachmentRules.MaxBytes / 3 * 4 + 8)
            throw new AgentToolException(400, "The file is over 10 MB.");
        try
        {
            return Convert.FromBase64String(body.Trim());
        }
        catch (FormatException)
        {
            throw new AgentToolException(400, "contentBase64 isn't valid base64.");
        }
    }

    private static async Task<object?> RegisterDevice(AgentCall c)
    {
        var saved = await c.Service<PushDeviceService>().RegisterAsync(c.Caller.UserId, c.String("platform") ?? "", c.String("token") ?? "",
            c.String("deviceName"));
        if (!saved.Ok)
            throw new AgentToolException(400, saved.Error!);
        return new { PushDeviceId = saved.Id, AlertsOn = c.Service<IPushSender>().IsConfigured };
    }

    private static async Task<object?> ListDevices(AgentCall c) =>
        (await c.Service<PushDeviceService>().ListAsync(c.Caller.UserId))
            .Select(d => new { d.PushDeviceId, d.Platform, d.DeviceName, d.Added, d.LastSeen, TokenEnds = d.Token.Length > 8 ? d.Token[^8..] : d.Token })
            .ToList();

    private static async Task<object?> UnregisterDevice(AgentCall c)
    {
        var token = c.String("token");
        var id = c.Int("pushDeviceId");
        if (string.IsNullOrWhiteSpace(token) && id is null)
            throw new AgentToolException(400, "Send token or pushDeviceId.");
        if (!await c.Service<PushDeviceService>().RemoveAsync(c.Caller.UserId, token, id))
            throw new AgentToolException(404, "That phone isn't registered for this login. list_push_devices gives the ids.");
        return new { Removed = true };
    }

    private static string Required(AgentCall c, string name) =>
        c.String(name) is { Length: > 0 } s && s.Trim().Length > 0 ? s.Trim() : throw new AgentToolException(400, $"{name} is required.");
}
