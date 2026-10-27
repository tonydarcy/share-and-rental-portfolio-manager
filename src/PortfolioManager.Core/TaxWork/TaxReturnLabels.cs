using System.Globalization;
using System.Text;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.TaxWork;

/// <summary>
/// One figure for the tax return. Code is the item and label (e.g. "11T"), empty where the
/// return's letters aren't given. Check is true where the item or letter should be confirmed
/// against this year's return before keying.
/// </summary>
public sealed record TaxReturnLabel(string Section, string Code, string Description, decimal Amount, string Source, bool Check = false);

public sealed record TaxReturnSchedule(string ReturnName, string EntityType, string ClientName, string? Abn, int Year, IReadOnlyList<TaxReturnLabel> Labels)
{
    public string YearLabel => FinancialYear.Label(Year);

    /// <summary>The figures for the entity's return, from the workpaper. Zero amounts are left out.</summary>
    public static TaxReturnSchedule For(TaxWorkpaper w)
    {
        var labels = w.EntityType switch
        {
            ClientEntityTypes.Company => Company(w),
            ClientEntityTypes.Trust => TrustOrPartnership(w, "Trust"),
            ClientEntityTypes.Partnership => TrustOrPartnership(w, "Partnership"),
            ClientEntityTypes.Smsf => Smsf(w),
            _ => Individual(w),
        };
        var name = w.EntityType switch
        {
            ClientEntityTypes.Company => "Company tax return",
            ClientEntityTypes.Trust => "Trust tax return",
            ClientEntityTypes.Partnership => "Partnership tax return",
            ClientEntityTypes.Smsf => "SMSF annual return",
            _ => "Individual tax return (investment items)",
        };
        return new(name, w.EntityType, w.Client.LegalName, w.Client.Abn, w.Year, labels.Where(l => l.Amount != 0).ToList());
    }

    // Interest on investment loans (account 5300) is a dividend deduction instead, at its deductible part
    private static decimal InterestExpenses(TaxWorkpaper w) =>
        w.Ledger.Movement(a => LedgerYear.IsOperatingExpense(a) && a.Number != LoanInterestYear.Account && a.Name.Contains("interest", StringComparison.OrdinalIgnoreCase));

    private static decimal DividendDeductions(TaxWorkpaper w, decimal interestExpense) =>
        w.Ledger.Expenses - interestExpense - w.Ledger.Movement(a => a.Number == LoanInterestYear.Account) + w.Loans.Deductible;

    private static IEnumerable<TaxReturnLabel> Individual(TaxWorkpaper w)
    {
        var i = w.Income;
        var c = w.Cgt;
        var interestExpense = InterestExpenses(w);
        return
        [
            new("Income", "10L", "Gross interest", i.Interest, "Payouts: interest"),
            new("Income", "10M", "TFN amounts withheld from gross interest", i.TfnOnInterest, "Payouts: tax withheld on interest-only payments"),
            new("Income", "11S", "Dividends: unfranked amount", i.Unfranked, "Payouts: unfranked"),
            new("Income", "11T", "Dividends: franked amount", i.Franked, "Payouts: franked"),
            new("Income", "11U", "Dividends: franking credit", i.DividendFrankingCredits, "Payouts: franking credits"),
            new("Income", "11V", "TFN amounts withheld from dividends", i.TfnOnDividends, "Payouts: other tax withheld"),
            new("Income", "13U", "Share of net income from trusts, less capital gains, foreign income and franked distributions", i.TrustIncome,
                i.Statements > 0 ? "Fund tax statements (unfranked, interest, other and NCMI) and payouts' trust income" : "Payouts: trust income. Split it by the fund's tax statement (franked, capital gains, foreign)", true),
            new("Income", "13C", "Franked distributions from trusts", i.TrustFranked, "Fund tax statements", true),
            new("Income", "13Q", "Share of franking credits from franked dividends", i.TrustFrankingCredits, "Fund tax statements", true),
            new("Employee share schemes", "12D", "Discount from taxed upfront schemes, eligible for reduction", w.Ess.UpfrontEligible,
                "ESS statements. The $1,000 reduction applies only if the income test is met", true),
            new("Employee share schemes", "12E", "Discount from taxed upfront schemes, not eligible for reduction", w.Ess.UpfrontNotEligible, "ESS statements", true),
            new("Employee share schemes", "12F", "Discount from deferral schemes", w.Ess.Deferral, "ESS statements", true),
            new("Employee share schemes", "12G", "Discount on ESS interests acquired before 1 July 2009, cessation time this year", w.Ess.Pre2009, "ESS statements", true),
            new("Employee share schemes", "12C", "TFN amounts withheld from discounts", w.Ess.TfnWithheld, "ESS statements", true),
            new("Employee share schemes", "12A", "Foreign source discounts", w.Ess.ForeignSource, "ESS statements: the foreign part of the discounts above", true),
            new("Business income", "P8", "Share trading: net profit or loss from shares held as trading stock", w.Trading?.Profit ?? 0,
                "Tax work > Shares as trading stock. Goes in the business and professional items schedule", true),
            new("Capital gains", "18H", "Total current year capital gains", c.GrossGains, "FIFO parcels, before losses and discount"),
            new("Capital gains", "18A", "Net capital gain", c.NetCapitalGain, "After capital losses and the 50% discount"),
            new("Capital gains", "18V", "Net capital losses carried forward to later income years", c.LossCarriedForward, "FIFO parcels"),
            new("Foreign income", "20M", "Other net foreign source income", i.ForeignIncome, "Fund tax statements: assessable foreign income", true),
            new("Foreign income", "20O", "Foreign income tax offsets", i.ForeignTax, "Payouts: foreign tax withheld, and fund tax statements. Foreign income from payouts goes at 20E/20M", true),
            new("Other income", "24Y", "Other income", i.Other, "Payouts: other amounts, such as crypto staking, airdrop and mining rewards", true),
            new("Rent", "21P", "Gross rent", w.Rental?.GrossRent ?? 0, "Tax work > Rental properties: the owner's share", true),
            new("Rent", "21Q", "Interest deductions", w.Rental?.Interest ?? 0, "Tax work > Rental properties", true),
            new("Rent", "21F", "Capital works deductions", w.Rental?.CapitalWorks ?? 0, "Tax work > Rental properties: Division 43", true),
            new("Rent", "21U", "Other rental deductions", w.Rental?.OtherDeductions ?? 0, "Tax work > Rental properties: other expenses and Division 40 decline in value", true),
            new("Deductions", "D7", "Interest deductions", interestExpense, "Ledger: expense accounts named interest", true),
            new("Deductions", "D8", "Dividend deductions", DividendDeductions(w, interestExpense),
                w.Loans.Deductible != 0 ? "Ledger: other investment expenses, and the deductible interest on investment loans (Debt > Loans)" : "Ledger: other investment expenses", true),
        ];
    }

    private static IEnumerable<TaxReturnLabel> Company(TaxWorkpaper w)
    {
        var i = w.Income;
        var c = w.Cgt;
        var t = w.Tax;
        return
        [
            new("Item 6 Income", "6F", "Gross interest", i.Interest, "Payouts: interest", true),
            new("Item 6 Income", "6H", "Total dividends (franked and unfranked, without franking credits)", i.Franked + i.Unfranked, "Payouts", true),
            new("Item 6 Income", "6E", "Gross distribution from trusts", i.TrustIncome + i.TrustFranked + i.ForeignIncome, "Payouts: trust income, and fund tax statements (without capital gains)", true),
            new("Item 6 Income", "6S", "Other gross income", i.Other, "Payouts: other amounts, such as crypto staking, airdrop and mining rewards", true),
            new("Item 6 Expenses", "", "Total expenses", w.Ledger.Expenses, "Ledger: expenses other than income tax", true),
            new("Item 6", "6T", "Total profit or loss", w.Reconciliation.ProfitBeforeTax, "Ledger: profit before income tax", true),
            new("Item 7 Reconciliation", "", "Less: accounting gains on investments", w.Ledger.InvestmentGains, "Ledger: 4500, 4600", true),
            new("Item 7 Reconciliation", "7A", "Net capital gain", c.NetCapitalGain, "FIFO parcels, after capital losses (no discount for a company)", true),
            new("Item 7 Reconciliation", "", "Franking credits", i.FrankingCredits, "Payouts. Already grossed up in the accounts' dividend income", true),
            new("Item 7 Reconciliation", "", "Non-deductible expenses added back", w.Data.Adjustments.Where(a => a.Kind == TaxAdjustmentKinds.AddBack).Sum(a => a.Amount), "Tax adjustments", true),
            new("Losses", "", "Tax losses deducted", w.Reconciliation.LossesApplied, "Tax settings", true),
            new("Losses", "", "Tax losses carried forward to later income years", w.Reconciliation.LossesCarriedForward, "Reconciliation", true),
            new("Losses", "", "Net capital losses carried forward to later income years", c.LossCarriedForward, "FIFO parcels", true),
            new("Calculation statement", "A", "Taxable or net income", t?.TaxableIncome ?? 0, "Reconciliation"),
            new("Calculation statement", "B", "Gross tax", t?.GrossTax ?? 0, $"At {t?.RatePercent ?? 0:0.##}%"),
            new("Calculation statement", "", "Foreign income tax offset", t?.ForeignOffset ?? 0, "Payouts: foreign tax", true),
            new("Calculation statement", "", "Franking tax offset", t?.FrankingOffset ?? 0, "Franking credits, up to the tax", true),
            new("Calculation statement", "", "Credit for TFN amounts withheld", t?.TfnCredits ?? 0, "Payouts: tax withheld", true),
            new("Calculation statement", "", "PAYG instalments raised", t?.PaygInstalments ?? 0, "Tax settings", true),
            new("Calculation statement", "", t?.Payable >= 0 ? "Amount due" : "Amount refundable", Math.Abs(t?.Payable ?? 0), "Tax payable"),
        ];
    }

    private static IEnumerable<TaxReturnLabel> TrustOrPartnership(TaxWorkpaper w, string kind)
    {
        var i = w.Income;
        var c = w.Cgt;
        var partnership = kind == "Partnership";
        return
        [
            new("Income", "", "Gross interest", i.Interest, "Payouts: interest", true),
            new("Income", "", "Dividends: unfranked amount", i.Unfranked, "Payouts: unfranked", true),
            new("Income", "", "Dividends: franked amount", i.Franked, "Payouts: franked", true),
            new("Income", "", "Dividends: franking credit", i.FrankingCredits, "Payouts: franking credits", true),
            new("Income", "", "TFN amounts withheld", i.TfnWithheld, "Payouts: tax withheld", true),
            new("Income", "", "Share of net income from trusts", i.TrustIncome + i.TrustFranked, "Payouts: trust income, and fund tax statements (without capital gains and foreign income)", true),
            new("Foreign income", "", "Foreign income from fund tax statements", i.ForeignIncome, "Fund tax statements", true),
            new("Income", "", "Other assessable income", i.Other, "Payouts: other amounts, such as crypto staking, airdrop and mining rewards", true),
            new("Capital gains", "", "Net capital gain", partnership ? 0 : c.NetCapitalGain, "FIFO parcels, after losses and the discount", true),
            new("Capital gains", "", "Net capital losses carried forward to later income years", partnership ? 0 : c.LossCarriedForward, "FIFO parcels", true),
            new("Foreign income", "", "Foreign income tax offsets", i.ForeignTax, "Payouts: foreign tax", true),
            new("Deductions", "", "Total deductions", w.Ledger.Expenses, "Ledger: expenses", true),
            new("Net income", "", partnership ? "Net income or loss of the partnership" : "Net income or loss of the trust", w.Reconciliation.TaxableIncome, "Reconciliation"),
            new("Losses", "", "Tax losses carried forward to later income years", w.Reconciliation.LossesCarriedForward, "Reconciliation", true),
        ];
    }

    private static IEnumerable<TaxReturnLabel> Smsf(TaxWorkpaper w)
    {
        var i = w.Income;
        var c = w.Cgt;
        var t = w.Tax;
        var cc = w.Smsf?.TaxInputs.Concessional ?? 0;
        return
        [
            new("Section B Income", "11A", "Net capital gain", c.NetCapitalGain, "FIFO parcels, after losses and the 33⅓% discount"),
            new("Section B Income", "11C", "Gross interest", i.Interest, "Payouts: interest"),
            new("Section B Income", "11J", "Unfranked dividend amount", i.Unfranked, "Payouts: unfranked"),
            new("Section B Income", "11K", "Franked dividend amount", i.Franked, "Payouts: franked"),
            new("Section B Income", "11L", "Dividend franking credit", i.FrankingCredits, "Payouts: franking credits"),
            new("Section B Income", "11M", "Gross trust distributions", i.TrustIncome + i.TrustFranked + i.ForeignIncome,
                i.Statements > 0 ? "Payouts: trust income, and fund tax statements (without capital gains)" : "Payouts: trust income", i.Statements > 0),
            new("Section B Income", "11S", "Other income", i.Other, "Payouts: other amounts, such as crypto staking, airdrop and mining rewards", true),
            new("Section B Income", "11R", "Assessable contributions", cc, "Member accounts: concessional. Split employer (R1) and personal (R2) contributions", true),
            new("Section B Income", "11Y", "Exempt current pension income", w.Reconciliation.ExemptPensionIncome, "ECPI percentage from the tax settings", true),
            new("Section C Deductions", "", "Total deductions", w.Ledger.Expenses, "Ledger: expenses. Split by label and take out the part relating to exempt income", true),
            new("Section D Income tax", "13A", "Taxable income", t?.TaxableIncome ?? 0, "Reconciliation"),
            new("Section D Income tax", "13T1", "Tax on taxable income", t?.GrossTax ?? 0, "At 15%"),
            new("Section D Income tax", "", "Foreign income tax offset", t?.ForeignOffset ?? 0, "Payouts: foreign tax", true),
            new("Section D Income tax", "13E1", "Complying fund's franking credits tax offset", t?.FrankingOffset ?? 0, "Payouts: franking credits", true),
            new("Section D Income tax", "", "Credit for TFN amounts withheld", t?.TfnCredits ?? 0, "Payouts: tax withheld", true),
            new("Section D Income tax", "13K", "PAYG instalments raised", t?.PaygInstalments ?? 0, "Tax settings", true),
            new("Section D Income tax", "13L", "Supervisory levy", t?.SupervisoryLevy ?? 0, "ATO levy", true),
            new("Section D Income tax", "13S", t?.Payable >= 0 ? "Amount due" : "Amount refundable", Math.Abs(t?.Payable ?? 0), "Tax payable", true),
            new("Losses", "", "Net capital losses carried forward to later income years", c.LossCarriedForward, "FIFO parcels", true),
            .. (w.Members ?? []).SelectMany(m => new TaxReturnLabel[]
            {
                new("Section F Member information", "A", $"{m.Member.Name}: employer contributions", m.Concessional,
                    "Member accounts: concessional. Move personal deductible contributions to B", true),
                new("Section F Member information", "B", $"{m.Member.Name}: personal contributions", m.NonConcessional,
                    "Member accounts: non-concessional, plus any personal deductible contributions", true),
                new("Section F Member information", "", $"{m.Member.Name}: retirement phase income stream payments", m.PensionPaid, "Member accounts: pension paid", true),
                new("Section F Member information", "S", $"{m.Member.Name}: closing account balance", m.Closing, "Member accounts after earnings and tax", true),
            }),
        ];
    }
}

/// <summary>
/// A file the tax return figures can be saved as. Add a class here for a package's own import
/// format when one is published; the label summary works with any package as a keying sheet.
/// </summary>
public interface ITaxReturnExport
{
    string Key { get; }
    string Name { get; }
    string Description { get; }
    string ContentType { get; }
    string FileName(TaxReturnSchedule schedule);
    byte[] Write(TaxReturnSchedule schedule);
}

/// <summary>Every label with its code, description, amount and where it came from, as CSV.</summary>
public sealed class LabelSummaryCsv : ITaxReturnExport
{
    public string Key => "labels-csv";
    public string Name => "Label summary (CSV)";
    public string Description => "One row per label, in return order. Opens in Excel; use it as the keying sheet for Xero Tax, HandiTax, LodgeiT or any other package.";
    public string ContentType => "text/csv";

    public string FileName(TaxReturnSchedule s)
    {
        var name = new string(s.ClientName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        return $"{(name.Length > 0 ? name : "client")}-{s.YearLabel}-tax-labels.csv";
    }

    public byte[] Write(TaxReturnSchedule s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Return,Year,Client,ABN,Section,Label,Description,Amount,Source,Check");
        foreach (var l in s.Labels)
        {
            sb.AppendJoin(',', [
                Csv(s.ReturnName), Csv(s.YearLabel), Csv(s.ClientName), Csv(s.Abn ?? ""), Csv(l.Section), Csv(l.Code), Csv(l.Description),
                l.Amount.ToString("0.00", CultureInfo.InvariantCulture), Csv(l.Source), l.Check ? "Confirm label" : "",
            ]);
            sb.AppendLine();
        }
        // Excel opens UTF-8 CSV correctly with a byte order mark
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public static string Csv(string value)
    {
        // A leading =, +, - or @ would be read as a formula
        if (value.Length > 0 && "=+-@".Contains(value[0]))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}

public static class TaxReturnExports
{
    public static readonly IReadOnlyList<ITaxReturnExport> All = [new LabelSummaryCsv()];

    public static ITaxReturnExport? Find(string? key) => All.FirstOrDefault(e => e.Key == key);

    /// <summary>What each package can take in, as checked in October 2026.</summary>
    public static readonly IReadOnlyList<(string Package, string Import)> Packages =
    [
        ("Xero Tax", "No import for return labels: it prefills from a Xero ledger and the ATO. Key the figures from the label summary."),
        ("HandiTax", "Its CSV import is for depreciation assets only. Key the figures from the label summary."),
        ("LodgeiT", "No documented import for return labels. Key the figures from the label summary."),
    ];
}
