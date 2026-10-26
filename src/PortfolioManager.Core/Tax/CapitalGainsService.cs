namespace PortfolioManager.Core.Tax;

public sealed record CapitalGainLine(
    string ExchangeCode,
    string StockCode,
    DateOnly? DateSold,
    string DateSoldText,
    decimal CostPerUnit,
    decimal TotalCost,
    decimal ProceedsPerUnit,
    decimal TotalProceeds,
    decimal GainPerUnit,
    decimal Gain);

public sealed record CapitalGainsTotal(decimal TotalCost, decimal TotalProceeds, decimal Gain);

public sealed record CapitalGainsReport(IReadOnlyList<CapitalGainLine> Lines, CapitalGainsTotal Total);

/// <summary>
/// Realised capital gains by sale, before any CGT discount. Each sale's parcels are matched as the tax figures match them
/// (<see cref="CgtDiscountService"/>): first in, first out unless parcels were chosen on the sale, after returns of capital.
/// </summary>
public sealed class CapitalGainsService(CgtDiscountService cgt)
{
    public async Task<CapitalGainsReport> GetAsync(int portfolioId, DateOnly from, DateOnly to)
    {
        var report = await cgt.GetReturnAsync(portfolioId, null, new ReportPeriod(null, from, to, null));
        return FromEvents(report.Events);
    }

    /// <summary>
    /// One line per sale (the parcels it took added together), sorted by date sold then holding. Returns of capital above a
    /// parcel's cost base, fund gains and property sales aren't share sales, so they're left out here.
    /// </summary>
    public static CapitalGainsReport FromEvents(IEnumerable<CgtEvent> events)
    {
        var lines = events
            .Where(e => e.Type == CgtEventType.Sale)
            .GroupBy(e => (e.SellTradeId, Exchange: e.ExchangeCode.Trim().ToUpperInvariant(), Code: e.StockCode.Trim().ToUpperInvariant(), e.Disposed))
            .Select(g =>
            {
                var units = g.Sum(e => e.Units);
                var cost = g.Sum(e => e.CostBase);
                var proceeds = g.Sum(e => e.Proceeds);
                return new CapitalGainLine(g.Key.Exchange, g.Key.Code, g.Key.Disposed, g.Key.Disposed.ToString("dd MMM yyyy"),
                    PerUnit(cost, units), cost, PerUnit(proceeds, units), proceeds, PerUnit(proceeds - cost, units), proceeds - cost);
            })
            .OrderBy(l => l.DateSold)
            .ThenBy(l => l.StockCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.ExchangeCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new CapitalGainsReport(lines, new CapitalGainsTotal(lines.Sum(l => l.TotalCost), lines.Sum(l => l.TotalProceeds), lines.Sum(l => l.Gain)));
    }

    private static decimal PerUnit(decimal amount, decimal units) => units == 0 ? 0 : Math.Round(amount / units, 4, MidpointRounding.AwayFromZero);
}
