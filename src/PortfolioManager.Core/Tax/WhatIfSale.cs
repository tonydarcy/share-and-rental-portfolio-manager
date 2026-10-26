using Dapper;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Regions;

namespace PortfolioManager.Core.Tax;

/// <summary>A sale that hasn't happened: what the What-if sale page asks.</summary>
public sealed record PlannedSale(string ExchangeCode, string StockCode, DateOnly Date, decimal Units, decimal Price, decimal Brokerage, ParcelMethod Method)
{
    public decimal Proceeds => Math.Round(Units * Price - Brokerage, 2, MidpointRounding.AwayFromZero);
    public decimal ProceedsPerUnit => Units > 0 ? (Units * Price - Brokerage) / Units : 0;
}

/// <summary>
/// The planned sale's parcels and gain, and the year's capital gains figures with and without it,
/// so the change in net capital gain (after losses and the discount) and the tax on it can be shown.
/// </summary>
public sealed record WhatIfResult(
    PlannedSale Sale, IReadOnlyList<HeldParcel> Held, IReadOnlyList<CgtEvent> Events, CgtSummary Before, CgtSummary After,
    ReportPeriod Year, decimal TaxRate, string? Error)
{
    public decimal UnitsHeld => Held.Sum(h => h.Units);
    public decimal Gain => Events.Sum(e => e.Gain);
    public decimal CostBase => Events.Sum(e => e.CostBase);
    public decimal NetGainChange => After.NetCapitalGain - Before.NetCapitalGain;

    /// <summary>The tax on the extra net capital gain at the rate given; a loss only saves tax if there are gains this year to absorb it.</summary>
    public decimal TaxChange => Math.Round(NetGainChange * TaxRate, 2, MidpointRounding.AwayFromZero);

    public decimal LossCarriedForwardChange => After.LossCarriedForward - Before.LossCarriedForward;
}

public static class WhatIf
{
    /// <summary>A trade id no real trade has, so the planned sale sorts after every trade on its day.</summary>
    public const int PlannedTradeId = int.MaxValue;

    /// <summary>
    /// A rough marginal rate to start from: companies 30% (25% for a base rate entity), complying super funds 15%,
    /// and 32% for anyone else (the 30% bracket plus the 2% Medicare levy). The page lets it be changed.
    /// </summary>
    public static decimal DefaultTaxRate(CgtDiscountRate rate) => rate.Kind switch
    {
        CgtEntityKind.Company => 0.30m,
        CgtEntityKind.SuperFund => 0.15m,
        CgtEntityKind.NotApplicable => 0.30m,
        _ => 0.32m,
    };

    /// <summary>Works out the planned sale against the portfolio's trades, saved parcel choices and returns of capital.</summary>
    public static WhatIfResult Run(PlannedSale sale, IReadOnlyList<TradeLot> trades, IReadOnlyDictionary<int, SaleParcelChoice> choices,
        IReadOnlyList<CapitalReturn> returns, CgtDiscountRate rate, decimal taxRate, YearStart? yearStart = null)
    {
        var fy = FinancialYear.EndYearFor(sale.Date, yearStart);
        var year = FinancialYear.Resolve(fy, null, null, sale.Date, yearStart);
        bool Same(string ex, string code) =>
            ex.Trim().Equals(sale.ExchangeCode, StringComparison.OrdinalIgnoreCase) && code.Trim().Equals(sale.StockCode, StringComparison.OrdinalIgnoreCase);

        var held = CgtParcels.Match(trades, sale.Date, returns, choices).Open
            .Where(p => Same(p.ExchangeCode, p.StockCode))
            .Select(p => new HeldParcel(p.BuyTradeId, p.Acquired, p.Units, p.CostPerUnit))
            .ToList();

        var without = CgtDiscountService.Build(CgtParcels.Match(trades, null, returns, choices), rate, year).Summary;
        string? error = sale.Units <= 0 ? "Enter the units to sell."
            : sale.Price < 0 || sale.Brokerage < 0 ? "The price and brokerage can't be negative."
            : held.Sum(h => h.Units) < sale.Units ? $"Only {held.Sum(h => h.Units):#,##0.########} units are held on {sale.Date:d MMM yyyy}."
            : null;
        if (error is not null)
            return new WhatIfResult(sale, held, [], without, without, year, taxRate, error);

        var picks = ParcelChoices.Plan(sale.Method, held, sale.Units, sale.Date, sale.ProceedsPerUnit, rate.Rate);
        var planned = new TradeLot(PlannedTradeId, sale.ExchangeCode, sale.StockCode, "Sell", sale.Date, sale.Units, sale.ProceedsPerUnit);
        var allChoices = new Dictionary<int, SaleParcelChoice>(choices) { [PlannedTradeId] = new(PlannedTradeId, sale.Method, picks) };
        var match = CgtParcels.Match([.. trades, planned], null, returns, allChoices);
        var with = CgtDiscountService.Build(match, rate, year).Summary;
        return new WhatIfResult(sale, held, match.Events.Where(e => e.SellTradeId == PlannedTradeId).ToList(), without, with, year, taxRate, null);
    }
}

/// <summary>Loads what <see cref="WhatIf.Run"/> needs, and the latest price to start from.</summary>
public sealed class WhatIfService(IDbConnectionFactory db, CgtDiscountService cgt, ParcelChoiceService choices, CorporateActionsService actions,
    Prices.FxRateService fx)
{
    public async Task<WhatIfResult> RunAsync(int portfolioId, PlannedSale sale, CgtDiscountRate rate, decimal taxRate, YearStart? yearStart = null) =>
        WhatIf.Run(sale, await cgt.LoadTradesAsync(portfolioId), await choices.GetAsync(portfolioId),
            await actions.GetTaxCostBaseLotsAsync(portfolioId), rate, taxRate, yearStart);

    /// <summary>The newest close on file for the stock (tblSharePriceCache): a market price, or one of this portfolio's own
    /// prices (Prices > Manual prices), never another portfolio's. A stock traded in another currency is converted to the
    /// portfolio's at the FX close for the price's date, as the cost base is. Null when there is no price (or rate).</summary>
    public Task<(decimal Price, DateOnly Date)?> LatestPriceAsync(int portfolioId, string exchangeCode, string stockCode) =>
        PriceAsync(portfolioId, exchangeCode, stockCode, null);

    /// <summary>As <see cref="LatestPriceAsync"/>, but the last close on or before the date (tblSharePrice keeps the history).</summary>
    public Task<(decimal Price, DateOnly Date)?> PriceOnAsync(int portfolioId, string exchangeCode, string stockCode, DateOnly date) =>
        PriceAsync(portfolioId, exchangeCode, stockCode, date);

    private async Task<(decimal Price, DateOnly Date)?> PriceAsync(int portfolioId, string exchangeCode, string stockCode, DateOnly? asAt)
    {
        await using var con = db.Create();
        var table = asAt is null ? "tblSharePriceCache" : "tblSharePrice";
        var row = await con.QueryFirstOrDefaultAsync<(double? Close, DateTime Date, string? Currency, string? Home)>($$"""
            SELECT TOP 1 dblClosePrice, dtmDetailsAsAtDate,
                   (SELECT TOP 1 T.strCurrency FROM tblShareTrades T WHERE T.iUserId = @portfolioId
                      AND T.strStockExchangeCode = @exchangeCode AND T.strStockCode = @stockCode
                    ORDER BY T.dtmDateOfTransaction DESC, T.iShareTradeId DESC),
                   (SELECT strPortfolioCurrency FROM tblUsers WHERE iUserId = @portfolioId)
            FROM {{table}}
            WHERE strStockExchangeCode = @exchangeCode AND strStockCode = @stockCode AND dblClosePrice > 0
              AND (iUserID IS NULL OR iUserID = 0 OR iUserID = @portfolioId)
              AND (@asAt IS NULL OR dtmDetailsAsAtDate < DATEADD(DAY, 1, @asAt))
            ORDER BY dtmDetailsAsAtDate DESC
            """, new { portfolioId, exchangeCode, stockCode, asAt = asAt?.ToDateTime(TimeOnly.MinValue) });
        if (row.Close is not > 0)
            return null;
        var date = DateOnly.FromDateTime(row.Date);
        var price = (decimal)row.Close.Value;
        var cur = (row.Currency ?? "").Trim().ToUpperInvariant();
        var home = string.IsNullOrWhiteSpace(row.Home) ? "AUD" : row.Home.Trim().ToUpperInvariant();
        if (cur.Length == 3 && cur != home)
        {
            if (await fx.GetAsync(cur, home, date) is not { } rate)
                return null;
            price = Math.Round(price / rate.Rate, 4, MidpointRounding.AwayFromZero);
        }
        return (price, date);
    }
}
