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

    /// <summary>
    /// The Medicare levy, with the low-income reduction for a single person: nil up to the lower threshold, then 10c a dollar
    /// over it until that reaches the full 2%. <paramref name="senior"/> uses the higher threshold for someone entitled to the
    /// seniors and pensioners tax offset. Family thresholds aren't applied.
    /// </summary>
    public decimal Medicare(decimal taxable, bool senior = false)
    {
        if (taxable <= 0)
            return 0;
        var full = taxable * MedicarePercent / 100m;
        var lower = MedicareLowIncome.For(Year).Threshold(senior);
        return taxable <= lower ? 0 : Math.Round(Math.Min(full, (taxable - lower) * 0.10m), 2);
    }
}

/// <summary>The Medicare levy low-income thresholds for a year (singles and seniors). Known is false when the nearest year's are used.</summary>
public sealed record MedicareLowIncome(int Year, decimal Single, decimal Senior, bool Known)
{
    private static readonly SortedDictionary<int, (decimal Single, decimal Senior)> ByYear = new()
    {
        [2023] = (24_276m, 38_365m),
        [2024] = (26_000m, 41_089m),
        [2025] = (27_222m, 43_020m),
    };

    public static MedicareLowIncome For(int year)
    {
        if (ByYear.TryGetValue(year, out var t))
            return new(year, t.Single, t.Senior, true);
        var nearest = year > ByYear.Keys.Last() ? ByYear.Keys.Last() : ByYear.Keys.First();
        return new(year, ByYear[nearest].Single, ByYear[nearest].Senior, false);
    }

    public decimal Threshold(bool senior) => senior ? Senior : Single;
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
    decimal ForeignTaxOffset,
    decimal EssUpfrontEligible = 0,
    decimal EssOtherDiscounts = 0,
    decimal ReportableFringeBenefits = 0,
    decimal ReportableSuper = 0,
    decimal NetInvestmentLoss = 0,
    bool Senior = false);

public sealed record PlanResult(PlanInputs In, decimal TaxableIncome, decimal IncomeTax, decimal Lito, decimal Medicare, decimal Payable, decimal MarginalRate,
    decimal ContributionsTax, decimal EssReduction = 0, decimal EssIncomeTest = 0)
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
        // ESS discounts are income; the $1,000 reduction depends on the income test, worked out before it.
        // Reportable super contributions include the deductible contribution.
        var before = p.OtherIncome + p.InvestmentIncome + p.RentalNet + p.NetCapitalGain + p.EssUpfrontEligible + p.EssOtherDiscounts
            - p.Deductions - p.ConcessionalDeduction;
        var incomeTest = Tax.EssReduction.IncomeTest(before, p.ReportableFringeBenefits, p.ReportableSuper + p.ConcessionalDeduction, p.NetInvestmentLoss);
        var reduction = p.EssUpfrontEligible > 0 ? Tax.EssReduction.For(p.EssUpfrontEligible, incomeTest) : 0;
        var taxable = Math.Max(0, Math.Round(before - reduction, 0));
        var tax = rates.Tax(taxable);
        var lito = Math.Min(IndividualRates.LowIncomeOffset(taxable), tax);
        var medicare = rates.Medicare(taxable, p.Senior);
        // Franking credits are refundable; the low income and foreign offsets only reduce tax to nil
        var beforeCredits = Math.Max(0, tax - lito - p.ForeignTaxOffset) + medicare;
        var payable = beforeCredits - p.FrankingCredits - p.Withheld;
        var contributionsRate = taxable + p.ConcessionalDeduction > Division293Threshold ? 0.30m : 0.15m;
        return new PlanResult(p, taxable, tax, lito, medicare, Math.Round(payable, 2), rates.MarginalRate(taxable),
            Math.Round(p.ConcessionalDeduction * contributionsRate, 2), reduction, incomeTest);
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

/// <summary>What changes in a company's or SMSF's year for the plan, on top of the year's figures so far.</summary>
public sealed record EntityPlanChanges(
    decimal ExtraIncome = 0,
    decimal ExtraDeductions = 0,
    decimal GainChange = 0,
    decimal ExtraContributions = 0,
    decimal ExtraFrankingCredits = 0);

/// <summary>A company's or complying SMSF's tax for the plan. Payable is negative for a refund.</summary>
public sealed record EntityPlanResult(decimal TaxableIncome, decimal RatePercent, decimal GrossTax, decimal ForeignOffset, decimal FrankingOffset,
    decimal ExcessFranking, decimal Credits, decimal Levy, decimal Payable)
{
    public decimal TaxAfterOffsets => GrossTax - ForeignOffset - FrankingOffset;

    /// <summary>For a company: the largest fully franked dividend the year's tax would frank (tax paid times (1 - rate) / rate).</summary>
    public decimal MaxFrankedDividend => RatePercent is > 0 and < 100 ? Math.Round(TaxAfterOffsets * (100m - RatePercent) / RatePercent, 2) : 0;
}

/// <summary>
/// Tax planning for a company or SMSF, starting from the year's taxable income so far. A company's franking offset can't make a
/// refund; a fund's can. In a fund, the exempt current pension income percentage takes that share of the extra investment income,
/// gains and deductions out of tax; contributions are always taxed (15%).
/// </summary>
public static class EntityPlanner
{
    public static EntityPlanResult Work(bool smsf, decimal ratePercent, decimal taxableSoFar, TaxIncomeFacts income, decimal paygInstalments,
        decimal ecpiPercent, EntityPlanChanges c)
    {
        var taxedShare = smsf ? 1m - Math.Clamp(ecpiPercent, 0, 100) / 100m : 1m;
        var taxable = Math.Max(0, Math.Floor(taxableSoFar + (c.ExtraIncome + c.GainChange - c.ExtraDeductions) * taxedShare + (smsf ? c.ExtraContributions : 0)));
        var gross = TaxReconciliation.Round(taxable * ratePercent / 100m);
        var foreign = Math.Min(income.ForeignTax, gross);
        var credits = income.FrankingCredits + c.ExtraFrankingCredits;
        var franking = smsf ? credits : Math.Min(credits, gross - foreign);
        var levy = smsf ? TaxCalculation.SmsfLevy : 0;
        var payable = gross - foreign - franking - income.TfnWithheld - paygInstalments + levy;
        return new EntityPlanResult(taxable, ratePercent, gross, foreign, franking, credits - franking, income.TfnWithheld + paygInstalments, levy, payable);
    }
}

/// <summary>One beneficiary in a trust distribution plan, with their income from outside the trust.</summary>
public sealed record PlanBeneficiary(string Name, string Type, decimal Percent, decimal OtherIncome);

/// <summary>A beneficiary's share and the extra tax it costs them: their tax with the share less their tax without it.</summary>
public sealed record BeneficiaryPlan(PlanBeneficiary Beneficiary, decimal Income, decimal NetGain, decimal Franking, decimal TaxWithout, decimal TaxWith)
{
    public decimal ExtraTax => TaxWith - TaxWithout;
}

public sealed record TrustPlan(IReadOnlyList<BeneficiaryPlan> Beneficiaries, decimal Unallocated, decimal TrusteeTax)
{
    /// <summary>Tax on the trust's income across the family: each beneficiary's extra tax, plus the trustee's on any income left undistributed.</summary>
    public decimal TotalTax => Beneficiaries.Sum(b => b.ExtraTax) + TrusteeTax;
}

/// <summary>
/// Tries a split of a discretionary trust's net income between beneficiaries. Each gets their percentage of the net income, of
/// its net capital gain and of the franking credits. An individual is taxed at resident rates with Medicare (the gain keeps the
/// trust's 50% discount); a company is taxed at its rate on the gain grossed back up (companies get no discount). Income no one
/// is entitled to is taxed to the trustee at the top rate plus Medicare (section 99A).
/// </summary>
public static class TrustPlanner
{
    public const decimal TrusteeRatePercent = 47m;

    public static TrustPlan Work(int year, decimal netIncome, decimal netCapitalGain, decimal frankingCredits, bool trustDiscount,
        IReadOnlyList<PlanBeneficiary> beneficiaries, decimal companyRatePercent = TaxCalculation.BaseRateEntityRate)
    {
        var rates = IndividualRates.For(year);
        var plans = new List<BeneficiaryPlan>();
        foreach (var b in beneficiaries.Where(b => b.Percent > 0))
        {
            var share = b.Percent / 100m;
            var gain = Math.Round(netCapitalGain * share, 2);
            var income = Math.Round((netIncome - netCapitalGain) * share, 2);
            var franking = Math.Round(frankingCredits * share, 2);
            decimal without, with;
            if (b.Type == BeneficiaryTypes.Company)
            {
                var grossGain = trustDiscount ? gain * 2 : gain;
                without = Math.Round(Math.Max(0, Math.Floor(b.OtherIncome)) * companyRatePercent / 100m, 2);
                var taxWith = Math.Round(Math.Max(0, Math.Floor(b.OtherIncome + income + grossGain)) * companyRatePercent / 100m, 2);
                with = taxWith - Math.Min(franking, taxWith);
            }
            else
            {
                without = TaxPlanner.Work(new PlanInputs(b.OtherIncome, 0, 0, 0, 0, 0, 0, 0, 0), rates).Payable;
                with = TaxPlanner.Work(new PlanInputs(b.OtherIncome, income, 0, gain, 0, 0, franking, 0, 0), rates).Payable;
            }
            plans.Add(new BeneficiaryPlan(b, income, gain, franking, without, with));
        }
        var allocated = Math.Min(100m, beneficiaries.Where(b => b.Percent > 0).Sum(b => b.Percent));
        var left = Math.Round(netIncome * (100m - allocated) / 100m, 2);
        var trusteeTax = left > 0 ? Math.Round(left * (TrusteeRatePercent + rates.MedicarePercent) / 100m, 2) : 0;
        return new TrustPlan(plans, left, trusteeTax);
    }
}
