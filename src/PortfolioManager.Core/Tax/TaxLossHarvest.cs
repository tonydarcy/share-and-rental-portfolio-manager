using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Regions;

namespace PortfolioManager.Core.Tax;

/// <summary>A holding with parcels worth less than they cost, and what selling just those parcels today would do.</summary>
public sealed record LossCandidate(string ExchangeCode, string StockCode, decimal UnitsHeld, decimal UnitsAtLoss, decimal Price, DateOnly PriceDate,
    decimal UnrealisedLoss, DateOnly LastBought, WhatIfResult Sale)
{
    /// <summary>Tax saved this year (a positive number) by selling the loss parcels.</summary>
    public decimal TaxSaved => -Sale.TaxChange;

    /// <summary>Bought within the last 30 days: selling now and buying back looks like a wash sale.</summary>
    public bool BoughtRecently(DateOnly today) => today.DayNumber - LastBought.DayNumber <= TaxLoss.WashSaleDays;
}

public sealed record TaxLossReport(DateOnly AsAt, ReportPeriod Year, CgtSummary SoFar, IReadOnlyList<LossCandidate> Candidates, IReadOnlyList<string> NoPrice)
{
    public decimal UnrealisedLoss => Candidates.Sum(c => c.UnrealisedLoss);
}

public static class TaxLoss
{
    /// <summary>A rule of thumb only: the ATO has no fixed period, and looks at whether the main purpose was the tax benefit.</summary>
    public const int WashSaleDays = 30;

    /// <summary>The parcels that would be sold at a loss at this price, and their total loss (a negative number).</summary>
    public static (decimal Units, decimal Loss) LossParcels(IEnumerable<OpenParcel> parcels, decimal price)
    {
        var losing = parcels.Where(p => p.CostPerUnit > price).ToList();
        return (losing.Sum(p => p.Units), Math.Round(losing.Sum(p => p.Units * (price - p.CostPerUnit)), 2, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// For each holding with parcels below cost, a what-if sale of just those parcels (smallest gain first, so the
    /// biggest losses go first) at today's price, against this year's gains so far. Largest tax saving first.
    /// </summary>
    public static TaxLossReport Find(IReadOnlyList<TradeLot> trades, IReadOnlyDictionary<int, SaleParcelChoice> choices,
        IReadOnlyList<CapitalReturn> returns, IReadOnlyDictionary<(string, string), (decimal Price, DateOnly Date)> prices,
        CgtDiscountRate rate, decimal taxRate, DateOnly today, YearStart? yearStart = null)
    {
        var open = CgtParcels.Match(trades, today, returns, choices).Open;
        var candidates = new List<LossCandidate>();
        var noPrice = new List<string>();
        WhatIfResult? any = null;
        foreach (var g in open.GroupBy(p => (p.ExchangeCode.Trim().ToUpperInvariant(), p.StockCode.Trim().ToUpperInvariant())).OrderBy(g => g.Key.Item2))
        {
            if (!prices.TryGetValue(g.Key, out var price))
            {
                noPrice.Add(g.Key.Item2);
                continue;
            }
            var (units, loss) = LossParcels(g, price.Price);
            if (units <= 0 || loss >= 0)
                continue;
            var sale = new PlannedSale(g.Key.Item1, g.Key.Item2, today, units, price.Price, 0, ParcelMethod.MinGain);
            var result = WhatIf.Run(sale, trades, choices, returns, rate, taxRate, yearStart);
            if (result.Error is not null)
                continue;
            any ??= result;
            candidates.Add(new LossCandidate(g.Key.Item1, g.Key.Item2, g.Sum(p => p.Units), units, price.Price, price.Date, loss,
                g.Max(p => p.Acquired), result));
        }

        // This year's figures so far: from any what-if (its "before"), or a sale of nothing
        var soFar = any?.Before ?? WhatIf.Run(new PlannedSale("", "", today, 0, 0, 0, ParcelMethod.Fifo), trades, choices, returns, rate, taxRate, yearStart).Before;
        var year = any?.Year ?? FinancialYear.Resolve(FinancialYear.EndYearFor(today, yearStart), null, null, today, yearStart);
        return new TaxLossReport(today, year, soFar,
            candidates.OrderByDescending(c => c.TaxSaved).ThenBy(c => c.UnrealisedLoss).ToList(), noPrice);
    }
}

/// <summary>Loads what <see cref="TaxLoss.Find"/> needs: the trades, parcel choices, capital returns and latest prices.</summary>
public sealed class TaxLossService(Data.IDbConnectionFactory db, CgtDiscountService cgt, ParcelChoiceService choices, CorporateActionsService actions,
    Prices.FxRateService fx)
{
    public async Task<TaxLossReport> GetAsync(int portfolioId, CgtDiscountRate rate, decimal taxRate, DateOnly today, YearStart? yearStart = null)
    {
        var prices = await LatestPricesAsync(portfolioId);
        return TaxLoss.Find(await cgt.LoadTradesAsync(portfolioId), await choices.GetAsync(portfolioId),
            await actions.GetTaxCostBaseLotsAsync(portfolioId), prices, rate, taxRate, today, yearStart);
    }

    // The newest market close for each stock the portfolio has traded, or the portfolio's own price (never another's),
    // in the portfolio's currency: a foreign close is converted at the FX close for its date. No rate, no price.
    private async Task<IReadOnlyDictionary<(string, string), (decimal Price, DateOnly Date)>> LatestPricesAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await Dapper.SqlMapper.QueryAsync<(string Exchange, string Code, double Close, DateTime Date, string? Currency, string? PortfolioCurrency)>(con, """
            SELECT S.strStockExchangeCode, S.strStockCode, L.dblClosePrice, L.dtmDetailsAsAtDate,
                   (SELECT TOP 1 T.strCurrency FROM tblShareTrades T WHERE T.iUserId = @portfolioId
                      AND T.strStockExchangeCode = S.strStockExchangeCode AND T.strStockCode = S.strStockCode
                    ORDER BY T.dtmDateOfTransaction DESC, T.iShareTradeId DESC),
                   (SELECT strPortfolioCurrency FROM tblUsers WHERE iUserId = @portfolioId)
            FROM (SELECT DISTINCT strStockExchangeCode, strStockCode FROM tblShareTrades WHERE iUserId = @portfolioId) S
            CROSS APPLY (SELECT TOP 1 C.dblClosePrice, C.dtmDetailsAsAtDate FROM tblSharePriceCache C
                         WHERE C.strStockExchangeCode = S.strStockExchangeCode AND C.strStockCode = S.strStockCode AND C.dblClosePrice > 0
                           AND (C.iUserID IS NULL OR C.iUserID = 0 OR C.iUserID = @portfolioId)
                         ORDER BY C.dtmDetailsAsAtDate DESC) L
            """, new { portfolioId });
        var map = new Dictionary<(string, string), (decimal, DateOnly)>();
        foreach (var r in rows)
        {
            var date = DateOnly.FromDateTime(r.Date);
            var price = (decimal)r.Close;
            var cur = (r.Currency ?? "").Trim().ToUpperInvariant();
            var home = string.IsNullOrWhiteSpace(r.PortfolioCurrency) ? "AUD" : r.PortfolioCurrency.Trim().ToUpperInvariant();
            if (cur.Length == 3 && cur != home)
            {
                if (await fx.GetAsync(cur, home, date) is not { } rate)
                    continue;
                price /= rate.Rate;
            }
            map[(r.Exchange.Trim().ToUpperInvariant(), r.Code.Trim().ToUpperInvariant())] = (price, date);
        }
        return map;
    }
}
