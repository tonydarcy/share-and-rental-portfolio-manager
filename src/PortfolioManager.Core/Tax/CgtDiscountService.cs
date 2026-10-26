using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Trades;

namespace PortfolioManager.Core.Tax;

/// <summary>
/// The CGT figures for a period. PriorYearLossesIncluded is false for custom dates, where losses
/// from earlier years aren't worked out.
/// </summary>
public sealed record CgtReturnReport(
    CgtSummary Summary,
    IReadOnlyList<CgtEvent> Events,
    bool PriorYearLossesIncluded,
    IReadOnlyList<ParcelProblem> Problems,
    int CapitalReturns = 0)
{
    public decimal TotalGain => Events.Sum(e => e.Gain);

    public decimal CapitalReturnGains => Events.Where(e => e.IsCapitalReturn).Sum(e => e.Gain);
}

/// <summary>
/// Applies the CGT discount using parcel dates matched from the trades list (TradesService), since
/// the legacy GetCapitalGainsTaxSummary has no purchase dates and nothing keeps tblCGTParcelMatching up to date.
/// Returns of capital (tblReturnsOfCapital) reduce the cost bases, with any excess a G1 gain.
/// </summary>
public sealed class CgtDiscountService(TradesService trades, CorporateActionsService actions, ParcelChoiceService parcelChoices,
    TaxWork.RentalService rentals)
{
    /// <summary>Saved parcel choices apply. Rental properties sold (Rental properties > Sale) are added as events of their own.</summary>
    public async Task<CgtReturnReport> GetReturnAsync(int portfolioId, string? entityType, ReportPeriod period)
    {
        var lots = await LoadTradesAsync(portfolioId);
        var returns = await actions.GetTaxCostBaseLotsAsync(portfolioId);
        var choices = await parcelChoices.GetAsync(portfolioId);
        var properties = await rentals.GetSaleEventsAsync(portfolioId);
        var match = CgtParcels.Match(lots, null, returns, choices);
        if (properties.Count > 0)
            match = match with { Events = [.. match.Events, .. properties] };
        return Build(match, CgtDiscount.RateFor(entityType), period);
    }

    /// <summary>Parcels still held at the date, with what can't be matched.</summary>
    public async Task<ParcelMatch> GetOpenParcelsAsync(int portfolioId, DateOnly asAt) =>
        CgtParcels.Match(await LoadTradesAsync(portfolioId), asAt, await actions.GetTaxCostBaseLotsAsync(portfolioId),
            await parcelChoices.GetAsync(portfolioId));

    public static CgtReturnReport Build(ParcelMatch match, CgtDiscountRate rate, ReportPeriod period)
    {
        bool InPeriod(CgtEvent e) => e.Disposed >= period.From && e.Disposed <= period.To;

        var inPeriod = match.Events.Where(InPeriod).ToList();
        var prior = period.FinancialYear is { } fy ? CgtDiscount.LossesBroughtForward(match.Events, fy, rate) : 0m;
        return new CgtReturnReport(
            CgtDiscount.Summarise(inPeriod, rate, prior),
            inPeriod,
            period.FinancialYear is not null,
            match.Problems.Where(p => p.Date <= period.To).ToList(),
            match.CapitalReturns);
    }

    /// <summary>All the portfolio's trades, oldest first, for parcel matching.</summary>
    public async Task<IReadOnlyList<TradeLot>> LoadTradesAsync(int portfolioId) =>
        (await trades.GetTradesAsync(portfolioId, null, oldestFirst: true))
            .Select(t => new TradeLot(t.TradeId, t.ExchangeCode, t.StockCode, t.Type, DateOnly.FromDateTime(t.Date),
                (decimal)t.Quantity, (decimal)t.CostBasisPerUnit))
            .ToList();
}
