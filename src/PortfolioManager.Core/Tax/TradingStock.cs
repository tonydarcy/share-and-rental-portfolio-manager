using System.Data;
using Dapper;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Tax;

/// <summary>How shares held as trading stock are valued at the year end (s70-45): each holding at cost, at market value, or at whichever is lower.</summary>
public static class TradingStockValuation
{
    public const string Cost = "Cost", Market = "Market", Lower = "Lower";

    public static readonly string[] All = [Cost, Market, Lower];

    public static string Text(string v) => v switch
    {
        Market => "Market value",
        Lower => "The lower of cost and market value, for each holding",
        _ => "Cost",
    };
}

/// <summary>A portfolio's shares are trading stock from FromYear (the financial year it ends) on; null when they're investments.</summary>
public sealed record ShareTraderSettings(int? FromYear, string Valuation)
{
    public static readonly ShareTraderSettings None = new(null, TradingStockValuation.Cost);

    public bool IsTraderIn(int year) => FromYear is { } y && year >= y;
}

/// <summary>One holding's trading stock figures for the year, in the portfolio's currency.</summary>
public sealed record TradingStockLine(
    string ExchangeCode, string StockCode, decimal OpeningUnits, decimal OpeningValue, decimal Purchases, decimal Sales,
    decimal ClosingUnits, decimal ClosingCost, decimal? ClosingMarket, decimal ClosingValue)
{
    /// <summary>Sales plus closing stock, less opening stock and purchases (s70-35).</summary>
    public decimal Profit => Sales + ClosingValue - OpeningValue - Purchases;
}

public sealed record TradingStockReport(int Year, string Valuation, bool FirstYear, IReadOnlyList<TradingStockLine> Lines, IReadOnlyList<string> Problems)
{
    public decimal OpeningValue => Lines.Sum(l => l.OpeningValue);
    public decimal Purchases => Lines.Sum(l => l.Purchases);
    public decimal Sales => Lines.Sum(l => l.Sales);
    public decimal ClosingValue => Lines.Sum(l => l.ClosingValue);
    public decimal Profit => Lines.Sum(l => l.Profit);
}

public static class TradingStock
{
    /// <summary>
    /// The year's trading profit. Opening stock is last year's closing value by the same method (at cost in the first
    /// year, when the shares start being trading stock). Purchases and sales are the year's trades at their cost and
    /// proceeds after brokerage; a demerger's new shares (demergerBuys) aren't a purchase, as their cost comes out of
    /// the old shares. A holding without a price at a year end is valued at cost, and said so.
    /// </summary>
    public static TradingStockReport Build(int year, DateOnly from, DateOnly to, ShareTraderSettings settings, IEnumerable<TradeLot> trades,
        IReadOnlySet<int> demergerBuys, IEnumerable<OpenParcel> opening, IEnumerable<OpenParcel> closing,
        IReadOnlyDictionary<(string, string), decimal> openingPrices, IReadOnlyDictionary<(string, string), decimal> closingPrices)
    {
        var first = settings.FromYear is not { } y || y >= year;
        var problems = new List<string>();
        var open = Totals(opening);
        var close = Totals(closing);
        var inYear = trades.Where(t => t.Date >= from && t.Date <= to).ToList();
        var keys = open.Keys.Concat(close.Keys).Concat(inYear.Select(t => Key(t.ExchangeCode, t.StockCode))).Distinct()
            .OrderBy(k => k.Item2, StringComparer.OrdinalIgnoreCase).ThenBy(k => k.Item1, StringComparer.OrdinalIgnoreCase);

        var lines = new List<TradingStockLine>();
        foreach (var k in keys)
        {
            var (ou, oc) = open.GetValueOrDefault(k);
            var (cu, cc) = close.GetValueOrDefault(k);
            var mine = inYear.Where(t => Key(t.ExchangeCode, t.StockCode) == k).ToList();
            var purchases = Cents(mine.Where(t => Is(t, "BUY") && !demergerBuys.Contains(t.TradeId)).Sum(t => Math.Abs(t.Quantity) * t.CostBasisPerUnit));
            var sales = Cents(mine.Where(t => Is(t, "SELL")).Sum(t => Math.Abs(t.Quantity) * t.CostBasisPerUnit));

            var openingValue = first ? oc : Value(k, ou, oc, openingPrices, settings.Valuation, from.AddDays(-1), problems).Value;
            var (closingValue, market) = Value(k, cu, cc, closingPrices, settings.Valuation, to, problems);
            lines.Add(new TradingStockLine(k.Item1, k.Item2, ou, openingValue, purchases, sales, cu, cc, market, closingValue));
        }
        return new TradingStockReport(year, settings.Valuation, first, lines.Where(l => l.OpeningUnits != 0 || l.ClosingUnits != 0 || l.Purchases != 0 || l.Sales != 0).ToList(), problems);
    }

    private static (decimal Value, decimal? Market) Value((string, string) k, decimal units, decimal cost, IReadOnlyDictionary<(string, string), decimal> prices,
        string valuation, DateOnly date, List<string> problems)
    {
        if (units == 0)
            return (0, 0);
        decimal? market = prices.TryGetValue(k, out var p) ? Cents(units * p) : null;
        if (valuation == TradingStockValuation.Cost)
            return (cost, market);
        if (market is not { } m)
        {
            problems.Add($"{k.Item2} has no price on {date:d MMM yyyy}, so it's valued at cost.");
            return (cost, null);
        }
        return (valuation == TradingStockValuation.Lower ? Math.Min(cost, m) : m, m);
    }

    private static Dictionary<(string, string), (decimal Units, decimal Cost)> Totals(IEnumerable<OpenParcel> parcels) =>
        parcels.GroupBy(p => Key(p.ExchangeCode, p.StockCode))
            .ToDictionary(g => g.Key, g => (g.Sum(p => p.Units), g.Sum(p => p.TotalCost)));

    public static (string, string) Key(string exchange, string code) => (exchange.Trim().ToUpperInvariant(), code.Trim().ToUpperInvariant());

    private static bool Is(TradeLot t, string type) => t.Type.Trim().Equals(type, StringComparison.OrdinalIgnoreCase);

    private static decimal Cents(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}

/// <summary>The share trader setting (tblShareTrader) and the year's trading stock figures.</summary>
public sealed class TradingStockService(IDbConnectionFactory db, CgtDiscountService cgt, CorporateActionsService actions, ParcelChoiceService choices,
    WhatIfService prices)
{
    /// <summary>None until the migration has been run.</summary>
    public async Task<ShareTraderSettings> GetSettingsAsync(int portfolioId)
    {
        await using var con = db.Create();
        if (await con.ExecuteScalarAsync<int>("SELECT CASE WHEN OBJECT_ID('dbo.uspGetShareTrader', 'P') IS NULL THEN 0 ELSE 1 END") == 0)
            return ShareTraderSettings.None;
        var row = await con.QueryFirstOrDefaultAsync<(int? FromYear, string? Valuation)>("uspGetShareTrader", new { iUserID = portfolioId },
            commandType: CommandType.StoredProcedure);
        return new ShareTraderSettings(row.FromYear, TradingStockValuation.All.Contains(row.Valuation) ? row.Valuation! : TradingStockValuation.Cost);
    }

    public async Task<ProcResult> SaveSettingsAsync(int portfolioId, ShareTraderSettings settings)
    {
        var old = await GetSettingsAsync(portfolioId);
        // The first year whose figures change: the earlier start, or for a new valuation the later of the two
        int?[] starts = [old.FromYear, settings.FromYear];
        var changed = old.FromYear == settings.FromYear ? settings.FromYear : starts.Where(s => s is not null).Min();
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspSaveShareTrader", new
        {
            iUserID = portfolioId, iFromYear = settings.FromYear, strValuation = settings.Valuation,
            dtmFirstYearEnd = changed is { } y ? FinancialYear.EndDate(y).ToDateTime(TimeOnly.MinValue) : (DateTime?)null,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    /// <summary>Null when the portfolio isn't a share trader in the year.</summary>
    public async Task<TradingStockReport?> GetYearAsync(int portfolioId, int year)
    {
        var settings = await GetSettingsAsync(portfolioId);
        if (!settings.IsTraderIn(year))
            return null;
        var from = FinancialYear.StartDate(year);
        var to = FinancialYear.EndDate(year);
        // Stock is at what it cost: returns of capital and fund tax statements don't lower it (the cash is income of a
        // trader), but demergers, takeovers and option exercises still move cost between holdings
        var trades = await cgt.LoadTradesAsync(portfolioId);
        var records = await actions.GetParcelTransfersAsync(portfolioId);
        var transfers = records.Select(t => t.ToLot()).OfType<CapitalReturn>().ToList();
        var picks = await choices.GetAsync(portfolioId);
        var opening = CgtParcels.Match(trades, from.AddDays(-1), transfers, picks).Open;
        var closing = CgtParcels.Match(trades, to, transfers, picks).Open;
        var demergerBuys = records.Where(t => t.Kind == ParcelTransfer.Demerger && t.BuyTradeId is not null).Select(t => t.BuyTradeId!.Value).ToHashSet();
        return TradingStock.Build(year, from, to, settings, trades, demergerBuys, opening, closing,
            await PricesAsync(portfolioId, opening, from.AddDays(-1)), await PricesAsync(portfolioId, closing, to));
    }

    private async Task<Dictionary<(string, string), decimal>> PricesAsync(int portfolioId, IEnumerable<OpenParcel> parcels, DateOnly date)
    {
        var map = new Dictionary<(string, string), decimal>();
        foreach (var k in parcels.Select(p => TradingStock.Key(p.ExchangeCode, p.StockCode)).Distinct())
            if (await prices.PriceOnAsync(portfolioId, k.Item1, k.Item2, date) is { } p)
                map[k] = p.Price;
        return map;
    }
}
