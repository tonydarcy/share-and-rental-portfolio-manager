namespace PortfolioManager.Core.Statements;

public enum AssetLine { Cash, Receivables, TaxCredits, Investments, PropertyPlant, Clearing, Other }

public enum LiabilityLine { Payables, TaxPayable, DistributionsPayable, Borrowings, Clearing, Other }

public enum RevenueLine { Dividends, Interest, TrustDistributions, Rent, Gains, Contributions, Other }

public enum ExpenseLine { Operating, IncomeTax }

/// <summary>
/// Capital: share capital, settled sum, units, partners' or owner's capital.
/// CurrentAccount: partners' current accounts. Retained: retained earnings, undistributed income.
/// Distributions: dividends paid, distributions to beneficiaries, drawings, benefits paid.
/// MemberBenefits: SMSF member accounts. Other: anything the name and number can't tell.
/// </summary>
public enum EquityKind { Capital, CurrentAccount, Retained, Distributions, MemberBenefits, Other }

/// <summary>
/// Maps chart of accounts entries to financial statement lines. The account number ranges follow the
/// system chart (1100 bank, 1300 investments, 1400 tax credits, 4100-4900 income, 5400 rental
/// expenses, 3100 capital); names decide where the numbers can't. The conventions are listed in
/// <see cref="Conventions"/> so the notes and the page can show them.
/// </summary>
public static class StatementClassifier
{
    public static readonly IReadOnlyList<string> Conventions =
    [
        "Assets: 1100-1199 cash at bank; 1200-1299 receivables; 1300-1399 investments (share cost base accounts); 1400-1499 tax credits (TFN withholding, franking credits, foreign tax); 1500-1899 property, plant and equipment. Outside those ranges the name decides (bank or cash, receivable or debtor, share or investment, franking or withholding, property or equipment).",
        "Liabilities, by name: tax, GST or PAYG is tax payable; distribution, dividend or beneficiary is distributions payable; loan, borrowing, mortgage, margin, overdraft or credit card is borrowings; payable, creditor or accrued is payables. Otherwise 2000-2499 payables and 2500-2899 borrowings.",
        "Any account named suspense or clearing (Trade Clearing / Suspense, Bank Import Suspense) is shown separately and flagged, because a balance there at year end is usually an unsettled trade or an unallocated bank line.",
        "Income: 4100 or interest; 4200 or dividend; 4300, distribution or trust; 4400 or rent; gain; contribution (SMSF). Everything else is other income. An expense account named income tax or tax expense is shown as income tax expense.",
        "Equity, by name first: drawing, dividend, distribution, benefits paid or pension paid is a distribution of profit; member, accumulation or benefit is a member benefit account; retained, accumulated or undistributed is retained earnings; current account is a partner's current account; capital, settled, settlement or unit is contributed capital. Then by number: 3000-3199 capital, 3200-3299 retained earnings, 3300-3399 distributions or drawings, 3400-3499 member benefits.",
        "Anything that still doesn't match is shown on an \"Other\" line rather than left out.",
    ];

    public static AssetLine ClassifyAsset(int? number, string name)
    {
        if (IsClearing(name)) return AssetLine.Clearing;
        switch (number)
        {
            case >= 1100 and <= 1199: return AssetLine.Cash;
            case >= 1200 and <= 1299: return AssetLine.Receivables;
            case >= 1300 and <= 1399: return AssetLine.Investments;
            case >= 1400 and <= 1499: return AssetLine.TaxCredits;
            case >= 1500 and <= 1899: return AssetLine.PropertyPlant;
        }
        if (Has(name, "franking", "withholding", "tax offset", "tax credit", "tax refund")) return AssetLine.TaxCredits;
        if (Has(name, "bank", "cash", "term deposit", "savings")) return AssetLine.Cash;
        if (Has(name, "receivable", "debtor", "prepa", "accrued")) return AssetLine.Receivables;
        if (Has(name, "cost base", "share", "investment", "securit", "managed fund")) return AssetLine.Investments;
        if (Has(name, "property", "plant", "equipment", "vehicle", "building", "land", "furniture")) return AssetLine.PropertyPlant;
        return AssetLine.Other;
    }

    public static LiabilityLine ClassifyLiability(int? number, string name)
    {
        if (IsClearing(name)) return LiabilityLine.Clearing;
        if (Has(name, "income tax", "tax payable", "gst", "payg", "provision for tax")) return LiabilityLine.TaxPayable;
        if (Has(name, "distribution", "dividend", "beneficiar")) return LiabilityLine.DistributionsPayable;
        if (Has(name, "loan", "borrow", "mortgage", "margin", "overdraft", "credit card")) return LiabilityLine.Borrowings;
        if (Has(name, "payable", "creditor", "accrued", "accrual")) return LiabilityLine.Payables;
        return number switch
        {
            >= 2000 and <= 2499 => LiabilityLine.Payables,
            >= 2500 and <= 2899 => LiabilityLine.Borrowings,
            _ => LiabilityLine.Other,
        };
    }

    public static RevenueLine ClassifyRevenue(int? number, string name)
    {
        switch (number)
        {
            case 4100: return RevenueLine.Interest;
            case 4200: return RevenueLine.Dividends;
            case 4300: return RevenueLine.TrustDistributions;
            case 4400: return RevenueLine.Rent;
        }
        if (Has(name, "contribution", "rollover", "roll-over", "roll over")) return RevenueLine.Contributions;
        if (Has(name, "dividend", "franking")) return RevenueLine.Dividends;
        if (Has(name, "interest")) return RevenueLine.Interest;
        if (Has(name, "distribution", "trust")) return RevenueLine.TrustDistributions;
        if (Has(name, "rent")) return RevenueLine.Rent;
        if (Has(name, "gain")) return RevenueLine.Gains;
        return RevenueLine.Other;
    }

    public static ExpenseLine ClassifyExpense(int? number, string name) =>
        Has(name, "income tax", "tax expense") ? ExpenseLine.IncomeTax : ExpenseLine.Operating;

    public static EquityKind ClassifyEquity(int? number, string name)
    {
        if (Has(name, "drawing", "dividend", "distribution", "benefits paid", "benefit paid", "pension paid", "pensions paid", "lump sum"))
            return EquityKind.Distributions;
        if (Has(name, "member", "accumulation", "benefit")) return EquityKind.MemberBenefits;
        if (Has(name, "retained", "accumulated", "undistributed")) return EquityKind.Retained;
        if (Has(name, "current account", "current a/c")) return EquityKind.CurrentAccount;
        if (Has(name, "capital", "settled", "settlement sum", "unitholder", "units")) return EquityKind.Capital;
        return number switch
        {
            >= 3000 and <= 3199 => EquityKind.Capital,
            >= 3200 and <= 3299 => EquityKind.Retained,
            >= 3300 and <= 3399 => EquityKind.Distributions,
            >= 3400 and <= 3499 => EquityKind.MemberBenefits,
            _ => EquityKind.Other,
        };
    }

    /// <summary>"BHP.ASX - Cost Base" is shown as "BHP.ASX".</summary>
    public static string InvestmentName(string name) =>
        name.EndsWith(" - Cost Base", StringComparison.OrdinalIgnoreCase) ? name[..^" - Cost Base".Length].Trim() : name;

    public static bool IsClearing(string name) => Has(name, "suspense", "clearing");

    private static bool Has(string name, params string[] words) =>
        words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
}
