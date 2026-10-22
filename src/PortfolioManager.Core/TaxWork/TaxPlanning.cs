using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.TaxWork;

/// <summary>An individual's resident tax rates for a year: the income each band starts at and its rate.</summary>
public sealed record IndividualRates(int Year, IReadOnlyList<(decimal From, decimal Rate)> Bands, decimal MedicarePercent, bool Known)
{
    /// <summary>
    /// 2024-25 and 2025-26: nil to $18,200, 16% to $45,000, 30% to $135,000, 37% to $190,000, 45% above. The 16% rate drops to
    /// 15% for 2026-27 and 14% from 2027-28 (legislated in 2025). Earlier and later years use the nearest known year.
    /// </summary>
    public static IndividualRates For(int year)
    {
        var bottom = year switch { <= 2026 => 0.16m, 2027 => 0.15m, _ => 0.14m };
        return new IndividualRates(year, [(0m, 0m), (18_200m, bottom), (45_000m, 0.30m), (135_000m, 0.37m), (190_000m, 0.45m)], 2m, year is >= 2025 and <= 2028);
    }

    public decimal Tax(decimal taxable)
    {
        decimal tax = 0;
        for (var i = 0; i < Bands.Count; i++)
        {
            var (from, rate) = Bands[i];
            var to = i + 1 < Bands.Count ? Bands[i + 1].From : decimal.MaxValue;
            if (taxable > from)
                tax += (Math.Min(taxable, to) - from) * rate;
        }
        return Math.Round(tax, 2);
    }

    public decimal MarginalRate(decimal taxable) => Bands.Last(b => taxable > b.From || b.From == 0).Rate;

    /// <summary>Low income tax offset: $700, less 5c a dollar over $37,500 to $45,000, then 1.5c a dollar over $45,000 (nil from $66,667).</summary>
    public static decimal LowIncomeOffset(decimal taxable)
    {
        decimal lito = taxable <= 37_500m ? 700m
            : taxable <= 45_000m ? 700m - (taxable - 37_500m) * 0.05m
            : 325m - (taxable - 45_000m) * 0.015m;
        return Math.Max(0, Math.Round(lito, 2));
    }

    public decimal Medicare(decimal taxable) => taxable > 0 ? Math.Round(taxable * MedicarePercent / 100m, 2) : 0;
}

/// <summary>What goes into one side of the plan.</summary>
public sealed record PlanInputs(
    decimal OtherIncome,
    decimal InvestmentIncome,
    decimal RentalNet,
    decimal NetCapitalGain,
    decimal Deductions,
    decimal ConcessionalDeduction,
    decimal FrankingCredits,
    decimal Withheld,
    decimal ForeignTaxOffset);

public sealed record PlanResult(PlanInputs In, decimal TaxableIncome, decimal IncomeTax, decimal Lito, decimal Medicare, decimal Payable, decimal MarginalRate,
    decimal ContributionsTax)
{
    /// <summary>Tax the person pays plus the 15% (or 30%) the fund pays on the deductible contribution.</summary>
    public decimal TotalTax => Payable + In.Withheld + ContributionsTax;

    /// <summary>Income plus the contribution over $250,000: the fund's 15% goes to 30% (Division 293, simplified).</summary>
    public bool Division293 => TaxableIncome + In.ConcessionalDeduction > TaxPlanner.Division293Threshold;
}

/// <summary>A what-if sale of shares this year.</summary>
public sealed record WhatIfSale(decimal Proceeds, decimal CostBase, bool HeldOverYear)
{
    public decimal Gain => Proceeds - CostBase;
}

public static class TaxPlanner
{
    public const decimal Division293Threshold = 250_000m;

    public static PlanResult Work(PlanInputs p, IndividualRates rates)
    {
        var taxable = Math.Max(0, Math.Round(p.OtherIncome + p.InvestmentIncome + p.RentalNet + p.NetCapitalGain - p.Deductions - p.ConcessionalDeduction, 0));
        var tax = rates.Tax(taxable);
        var lito = Math.Min(IndividualRates.LowIncomeOffset(taxable), tax);
        var medicare = rates.Medicare(taxable);
        // Franking credits are refundable; the low income and foreign offsets only reduce tax to nil
        var beforeCredits = Math.Max(0, tax - lito - p.ForeignTaxOffset) + medicare;
        var payable = beforeCredits - p.FrankingCredits - p.Withheld;
        var contributionsRate = taxable + p.ConcessionalDeduction > Division293Threshold ? 0.30m : 0.15m;
        return new PlanResult(p, taxable, tax, lito, medicare, Math.Round(payable, 2), rates.MarginalRate(taxable),
            Math.Round(p.ConcessionalDeduction * contributionsRate, 2));
    }

    /// <summary>
    /// The net capital gain with a what-if sale added: losses (this year's and those brought forward) go against other gains first,
    /// then discount gains, then the discount comes off what's left of the discount gains.
    /// </summary>
    public static decimal NetGainWith(CgtSummary c, WhatIfSale? sale)
    {
        if (sale is null || sale.Gain == 0)
            return c.NetCapitalGain;
        var discountGains = c.DiscountGains + (sale.Gain > 0 && sale.HeldOverYear ? sale.Gain : 0);
        var otherGains = c.OtherGains + (sale.Gain > 0 && !sale.HeldOverYear ? sale.Gain : 0);
        var losses = c.CurrentYearLosses + c.PriorYearLosses + (sale.Gain < 0 ? -sale.Gain : 0);
        var againstOther = Math.Min(losses, otherGains);
        var againstDiscount = Math.Min(losses - againstOther, discountGains);
        var rate = c.Rate.Applies ? c.Rate.Rate : 0;
        return Math.Round(otherGains - againstOther + (discountGains - againstDiscount) * (1 - rate), 2);
    }
}
