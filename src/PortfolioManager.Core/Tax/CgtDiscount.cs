using PortfolioManager.Core.Regions;

namespace PortfolioManager.Core.Tax;

/// <summary>NotApplicable is a portfolio outside Australia, where no CGT discount is worked out.</summary>
public enum CgtEntityKind { Individual, Trust, SuperFund, Company, Unknown, NotApplicable }

/// <summary>The CGT discount for a portfolio's entity type. Assumed is true when the type wasn't recognised and 50% was used.</summary>
public sealed record CgtDiscountRate(CgtEntityKind Kind, decimal Rate, bool Assumed)
{
    /// <summary>No discount, because the portfolio isn't in Australia.</summary>
    public static readonly CgtDiscountRate None = new(CgtEntityKind.NotApplicable, 0m, false);

    public bool Applies => Kind != CgtEntityKind.NotApplicable;

    public string RateText => Kind == CgtEntityKind.SuperFund ? "33⅓%" : $"{Rate * 100:0.##}%";
}

public enum CgtEventType
{
    /// <summary>CGT event A1: shares sold.</summary>
    Sale,

    /// <summary>CGT event G1: a return of capital more than the parcel's cost base. Proceeds is the excess.</summary>
    CapitalReturnExcess,

    /// <summary>A capital gain distributed by a fund (its annual tax statement). Discount gains are grossed up and dated
    /// as held over a year, so the holder's own discount applies; other gains are not discounted.</summary>
    FundCapitalGain,
}

/// <summary>
/// Shares from one purchase sold in one sale, which is one CGT event, or the part of a return of
/// capital that one parcel's cost base couldn't absorb (G1). Amounts are in dollars and cents.
/// </summary>
public sealed record CgtEvent(
    string ExchangeCode,
    string StockCode,
    DateOnly Acquired,
    DateOnly Disposed,
    decimal Units,
    decimal CostBase,
    decimal Proceeds,
    CgtEventType Type = CgtEventType.Sale,
    int SellTradeId = 0,
    int BuyTradeId = 0)
{
    public decimal Gain => Proceeds - CostBase;

    public bool IsCapitalReturn => Type == CgtEventType.CapitalReturnExcess;

    public bool IsFundGain => Type == CgtEventType.FundCapitalGain;

    public bool DiscountEligible => CgtDiscount.Qualifies(Acquired, Disposed);
}

/// <summary>
/// The capital gains figures for a tax return. Losses go against other gains first, then discount
/// gains, and the discount is taken off the discount gains that are left.
/// </summary>
public sealed record CgtSummary(
    CgtDiscountRate Rate,
    decimal DiscountGains,
    decimal OtherGains,
    decimal CurrentYearLosses,
    decimal PriorYearLosses,
    decimal LossesAppliedToOtherGains,
    decimal LossesAppliedToDiscountGains,
    decimal Discount,
    decimal NetCapitalGain,
    decimal LossCarriedForward)
{
    public decimal GrossGains => DiscountGains + OtherGains;

    public decimal LossesApplied => LossesAppliedToOtherGains + LossesAppliedToDiscountGains;
}

/// <summary>The Australian CGT discount: who gets it, when a parcel qualifies, and how losses are applied.</summary>
public static class CgtDiscount
{
    /// <summary>
    /// Individuals and trusts get 50%, complying super funds 33⅓% and companies nothing. The legacy
    /// site offers Individual, Company, Superfund and Other, and the profile page takes free text,
    /// so anything else (Other included) is treated as 50% and flagged.
    /// </summary>
    public static CgtDiscountRate RateFor(string? entityType) => RateFor(entityType, RegionSettings.Current);

    /// <summary>The discount for the entity type, or none when the portfolio isn't in Australia.</summary>
    public static CgtDiscountRate RateFor(string? entityType, RegionSettings region)
    {
        if (!region.IsAustralia)
            return CgtDiscountRate.None;
        var t = new string((entityType ?? "").ToLowerInvariant().Where(char.IsLetter).ToArray());
        if (t.Contains("super") || t.Contains("smsf"))
            return new(CgtEntityKind.SuperFund, 1m / 3m, false);
        if (t.Contains("trust"))
            return new(CgtEntityKind.Trust, 0.5m, false);
        if (t.Contains("company") || t.Contains("ptyltd") || t.Contains("corporat"))
            return new(CgtEntityKind.Company, 0m, false);
        if (t.Contains("individual") || t is "person" or "personal")
            return new(CgtEntityKind.Individual, 0.5m, false);
        return new(CgtEntityKind.Unknown, 0.5m, true);
    }

    /// <summary>
    /// The first sale date that qualifies. The parcel must be held for more than 12 months, not
    /// counting the days it was bought and sold, so that's the day after the anniversary. A parcel
    /// bought on 29 February has its anniversary on 28 February in other years.
    /// </summary>
    public static DateOnly EligibleFrom(DateOnly acquired) => acquired.AddYears(1).AddDays(1);

    public static bool Qualifies(DateOnly acquired, DateOnly disposed) => disposed >= EligibleFrom(acquired);

    /// <summary>Works out the return figures for one year's CGT events, given losses brought forward from earlier years.</summary>
    public static CgtSummary Summarise(IEnumerable<CgtEvent> events, CgtDiscountRate rate, decimal priorYearLosses = 0m)
    {
        decimal discountGains = 0, otherGains = 0, losses = 0;
        foreach (var e in events)
        {
            if (e.Gain < 0)
                losses -= e.Gain;
            else if (e.DiscountEligible)
                discountGains += e.Gain;
            else
                otherGains += e.Gain;
        }

        var available = losses + Math.Max(priorYearLosses, 0m);
        var toOther = Math.Min(available, otherGains);
        available -= toOther;
        var toDiscount = Math.Min(available, discountGains);
        available -= toDiscount;

        var discountGainsLeft = discountGains - toDiscount;
        var discount = Math.Round(discountGainsLeft * rate.Rate, 2, MidpointRounding.AwayFromZero);
        var net = otherGains - toOther + discountGainsLeft - discount;

        return new CgtSummary(rate, discountGains, otherGains, losses, Math.Max(priorYearLosses, 0m),
            toOther, toDiscount, discount, net, available);
    }

    /// <summary>
    /// Net capital losses carried into a financial year, worked out by running each earlier year's
    /// sales through Summarise in order.
    /// </summary>
    public static decimal LossesBroughtForward(IEnumerable<CgtEvent> events, int financialYearEnd, CgtDiscountRate rate)
    {
        var carried = 0m;
        foreach (var year in events
            .GroupBy(e => FinancialYear.EndYearFor(e.Disposed))
            .Where(g => g.Key < financialYearEnd)
            .OrderBy(g => g.Key))
        {
            carried = Summarise(year, rate, carried).LossCarriedForward;
        }
        return carried;
    }
}
