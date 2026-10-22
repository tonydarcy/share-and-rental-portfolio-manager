using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Statements;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.TaxWork;

/// <summary>
/// The year's investment income from the payouts. TFN amounts withheld are split between interest
/// (a payout with interest and no dividend) and dividends and distributions (everything else).
/// Funds with an annual tax statement for the year use its figures instead of their payouts (<see cref="WithStatements"/>):
/// their franked distributions are TrustFranked, their franking credits are in FrankingCredits and also TrustFrankingCredits,
/// their other Australian income is TrustIncome, and ForeignIncome is their assessable foreign income.
/// StatementAdjustment is the attributed income less the distributions the accounts recorded for those funds.
/// </summary>
public sealed record TaxIncomeFacts(
    decimal Interest,
    decimal Unfranked,
    decimal Franked,
    decimal FrankingCredits,
    decimal TrustIncome,
    decimal ForeignTax,
    decimal TfnOnInterest,
    decimal TfnOnDividends,
    decimal Other,
    decimal TrustFranked = 0,
    decimal TrustFrankingCredits = 0,
    decimal ForeignIncome = 0,
    decimal StatementAdjustment = 0,
    int Statements = 0)
{
    public static readonly TaxIncomeFacts None = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    public decimal TfnWithheld => TfnOnInterest + TfnOnDividends;

    /// <summary>Franking credits on dividends paid directly (not through a fund's statement).</summary>
    public decimal DividendFrankingCredits => FrankingCredits - TrustFrankingCredits;

    /// <summary>
    /// The payouts of funds with no statement for the year, plus the statements' figures. The adjustment compares each
    /// statement's assessable income (without capital gains, which the net capital gain covers) with what the accounts
    /// took up for that fund: its payouts' totals and franking credits.
    /// </summary>
    public static TaxIncomeFacts WithStatements(IEnumerable<Payout> payouts, IEnumerable<FundTaxStatement> statements, int year)
    {
        var all = payouts.ToList();
        var (others, those) = FundTaxStatements.Split(all, statements, year);
        var f = From(others);
        if (those.Count == 0)
            return f;
        var inAccounts = all.Sum(p => p.Total + p.FrankingCredit) - others.Sum(p => p.Total + p.FrankingCredit);
        return f with
        {
            FrankingCredits = f.FrankingCredits + those.Sum(s => s.FrankingCredits),
            TrustIncome = f.TrustIncome + those.Sum(s => s.Unfranked + s.Interest + s.OtherIncome + s.Ncmi),
            ForeignTax = f.ForeignTax + those.Sum(s => s.ForeignTaxCredits),
            TfnOnDividends = f.TfnOnDividends + those.Sum(s => s.TfnWithheld),
            TrustFranked = those.Sum(s => s.Franked),
            TrustFrankingCredits = those.Sum(s => s.FrankingCredits),
            ForeignIncome = those.Sum(s => s.ForeignIncome),
            StatementAdjustment = those.Sum(s => s.AssessableIncome) - inAccounts,
            Statements = those.Count,
        };
    }

    public static TaxIncomeFacts From(IEnumerable<Payout> payouts)
    {
        decimal interest = 0, unfranked = 0, franked = 0, credits = 0, trust = 0, foreign = 0, tfnInterest = 0, tfnDividends = 0, other = 0;
        foreach (var p in payouts)
        {
            interest += p.Interest;
            unfranked += p.Unfranked;
            franked += p.Franked;
            credits += p.FrankingCredit;
            trust += p.TrustIncome;
            foreign += p.ForeignTax;
            other += p.Other;
            if (p.Interest != 0 && p.Franked == 0 && p.Unfranked == 0 && p.TrustIncome == 0)
                tfnInterest += p.TaxWithheld;
            else
                tfnDividends += p.TaxWithheld;
        }
        return new(interest, unfranked, franked, credits, trust, foreign, tfnInterest, tfnDividends, other);
    }
}

/// <summary>The ledger at the end of last year and this year, so each account's movement is the year's figure.</summary>
public sealed record LedgerYear(LedgerSnapshot Opening, LedgerSnapshot Closing)
{
    public IReadOnlyList<LedgerBalance> Accounts { get; } = Opening.Accounts.Concat(Closing.Accounts)
        .GroupBy(a => a.Key).Select(g => g.First())
        .OrderBy(a => a.Number ?? int.MaxValue).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public decimal Movement(LedgerBalance a) => Closing.Balance(a.Key) - Opening.Balance(a.Key);

    public decimal Movement(Func<LedgerBalance, bool> which) => Accounts.Where(which).Sum(Movement);

    public decimal Balance(int number) => Accounts.Where(a => a.Number == number).Sum(a => Closing.Balance(a.Key));

    public decimal Revenue => Movement(a => a.Type == "Revenue");

    /// <summary>Expenses other than income tax (current or deferred).</summary>
    public decimal Expenses => Movement(IsOperatingExpense);

    public decimal IncomeTaxExpense => Movement(a => a.Type == "Expense" && !IsOperatingExpense(a));

    public decimal ProfitBeforeTax => Revenue - Expenses;

    /// <summary>Realised and unrealised gains on investments (4500, 4600 and any revenue account named gain).</summary>
    public decimal InvestmentGains => Movement(a => a.Type == "Revenue" && StatementClassifier.ClassifyRevenue(a.Number, a.Name) == RevenueLine.Gains);

    /// <summary>Contributions and rollovers credited to an SMSF's revenue.</summary>
    public decimal Contributions => Movement(a => a.Type == "Revenue" && StatementClassifier.ClassifyRevenue(a.Number, a.Name) == RevenueLine.Contributions);

    public static bool IsOperatingExpense(LedgerBalance a) =>
        a.Type == "Expense" && StatementClassifier.ClassifyExpense(a.Number, a.Name) == ExpenseLine.Operating;

    public static readonly LedgerYear Empty = new(new LedgerSnapshot(default, [], 0, 0), new LedgerSnapshot(default, [], 0, 0));
}

public enum TaxRecStyle { Item, Subtotal, Total, Info }

/// <summary>One line of the reconciliation. Info lines explain a figure but aren't added in.</summary>
public sealed record TaxRecLine(string Label, decimal Amount, string? Note = null, TaxRecStyle Style = TaxRecStyle.Item);

/// <summary>SMSF figures the reconciliation needs from the member accounts.</summary>
public sealed record SmsfTaxInputs(decimal Concessional, decimal NonConcessional, decimal Rollovers)
{
    public static readonly SmsfTaxInputs None = new(0, 0, 0);
}

/// <summary>Accounting profit to taxable income (net income for a trust or partnership).</summary>
public sealed record TaxReconciliation(
    string EntityType,
    IReadOnlyList<TaxRecLine> Lines,
    decimal ProfitBeforeTax,
    decimal BeforeLosses,
    decimal LossesApplied,
    decimal TaxableIncome,
    decimal LossesCarriedForward,
    decimal ExemptPensionIncome,
    CgtSummary Cgt)
{
    public string ResultLabel => ResultLabelFor(EntityType);

    public static string ResultLabelFor(string entityType) => entityType switch
    {
        ClientEntityTypes.Trust => "Net income of the trust (s95)",
        ClientEntityTypes.Partnership => "Net income of the partnership (s90)",
        _ => "Taxable income",
    };

    /// <summary>
    /// Starts from profit before income tax, takes out the accounting gains on investments and puts
    /// the net capital gain in their place, applies the adjustments (and for an SMSF the
    /// non-assessable contributions and exempt current pension income), then tax losses brought
    /// forward. Franking credits are already grossed up in dividend income (4200), so they're shown
    /// but not added again. A partnership's capital gains belong to the partners, so they're left out.
    /// </summary>
    public static TaxReconciliation Build(
        string entityType,
        LedgerYear ledger,
        CgtSummary cgt,
        TaxIncomeFacts income,
        TaxYearData data,
        SmsfTaxInputs? smsf = null,
        decimal? tradingProfit = null)
    {
        var lines = new List<TaxRecLine>();
        var profit = ledger.ProfitBeforeTax;
        lines.Add(new("Profit before income tax (from the accounts)", profit, Style: TaxRecStyle.Subtotal));

        var running = profit;
        void Add(string label, decimal amount, string? note = null)
        {
            if (amount == 0) return;
            lines.Add(new(label, amount, note));
            running += amount;
        }

        Add(ledger.InvestmentGains >= 0 ? "Less: gains on investments in the accounts" : "Add: losses on investments in the accounts",
            -ledger.InvestmentGains, "Realised and unrealised gains are replaced by the net capital gain below.");

        var partnership = entityType == ClientEntityTypes.Partnership;
        if (tradingProfit is { } tp)
            Add(tp >= 0 ? "Add: profit on shares held as trading stock" : "Less: loss on shares held as trading stock", tp,
                "Sales plus closing stock, less opening stock and purchases (Tax work > Shares as trading stock). Trading stock has no capital gains or CGT discount.");
        else if (partnership)
        {
            if (cgt.NetCapitalGain != 0)
                lines.Add(new("Net capital gain (made by the partners, not included here)", cgt.NetCapitalGain, null, TaxRecStyle.Info));
        }
        else
            Add("Add: net capital gain", cgt.NetCapitalGain,
                cgt.Discount != 0 ? $"After capital losses and the {cgt.Rate.RateText} CGT discount of {cgt.Discount:#,##0.00}." : "After capital losses.");

        if (income.DividendFrankingCredits != 0)
            lines.Add(new("Franking credits (already included in dividend income)", income.DividendFrankingCredits, null, TaxRecStyle.Info));
        Add("Fund tax statements: income attributed less distributions in the accounts", income.StatementAdjustment,
            $"{income.Statements} fund statement(s). Their capital gains are in the net capital gain above, and their cost base changes in the parcels.");

        foreach (var a in data.Adjustments)
            Add($"{TaxAdjustmentKinds.Label(a.Kind)}: {a.Description}", a.Signed);

        decimal ecpi = 0;
        if (entityType == ClientEntityTypes.Smsf && smsf is not null)
        {
            // Contributions are either revenue (take out the ones that aren't assessable) or credited
            // straight to the member accounts in equity (add the assessable ones)
            if (ledger.Contributions == 0)
                Add("Add: concessional contributions (credited to member accounts, not revenue)", smsf.Concessional);
            else
                Add("Less: non-concessional contributions and rollovers (not assessable)", -(smsf.NonConcessional + smsf.Rollovers));
            if (ledger.Contributions != 0 && ledger.Contributions < smsf.Concessional + smsf.NonConcessional + smsf.Rollovers)
                lines.Add(new("Contributions in the accounts are less than the member records show", ledger.Contributions, null, TaxRecStyle.Info));

            if (data.Settings.EcpiPercent is { } pct and > 0)
            {
                // ECPI is the pension share of income other than assessable contributions; the same
                // share of the expenses isn't deductible (proportionate method)
                var expenseAddBacks = data.Adjustments.Where(a => a.Kind == TaxAdjustmentKinds.AddBack).Sum(a => a.Amount);
                var deductions = ledger.Expenses - expenseAddBacks + data.Adjustments.Where(a => a.Kind == TaxAdjustmentKinds.Deduction).Sum(a => a.Amount);
                var income0 = running + deductions - smsf.Concessional;
                ecpi = Round(Math.Max(income0, 0) * pct / 100m);
                Add($"Less: exempt current pension income ({pct:0.##}%)", -ecpi);
                Add($"Add: expenses relating to exempt income ({pct:0.##}%)", Round(Math.Max(deductions, 0) * pct / 100m));
            }
        }

        var before = running;
        lines.Add(new("Taxable income before losses", before, Style: TaxRecStyle.Subtotal));

        var available = Math.Max(data.Settings.TaxLossesBroughtForward, 0);
        var applied = Math.Min(available, Math.Max(before, 0));
        Add("Less: tax losses brought forward", -applied);

        var result = before - applied;
        lines.Add(new(ResultLabelFor(entityType), result, Style: TaxRecStyle.Total));

        var carried = available - applied + (result < 0 ? -result : 0);
        if (carried != 0)
            lines.Add(new(partnership && result < 0 ? "Loss to be distributed to the partners" : "Tax losses carried forward", partnership && result < 0 ? -result : carried, null, TaxRecStyle.Info));

        // A loss stays in a company, trust or fund (carried forward); a partnership's goes to the partners
        var taxable = result > 0 || partnership ? result : 0;
        return new TaxReconciliation(entityType, lines, profit, before, applied, taxable, partnership ? 0 : carried, ecpi, cgt);
    }

    internal static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}
