using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Statements;

namespace PortfolioManager.Tests;

public class StatementClassifierTests
{
    [Theory]
    [InlineData(1101, "Westpac Everyday", AssetLine.Cash)]
    [InlineData(1301, "BHP.ASX - Cost Base", AssetLine.Investments)]
    [InlineData(1410, "Franking Credits Receivable", AssetLine.TaxCredits)]
    [InlineData(1400, "TFN Withholding Credits", AssetLine.TaxCredits)]
    [InlineData(1250, "Loan to director", AssetLine.Receivables)]
    [InlineData(1600, "Rental property", AssetLine.PropertyPlant)]
    [InlineData(9000, "ING Savings", AssetLine.Cash)]
    [InlineData(9001, "Franking credits 2025", AssetLine.TaxCredits)]
    [InlineData(9002, "Something odd", AssetLine.Other)]
    [InlineData(1100, "Bank Import Suspense", AssetLine.Clearing)]
    public void Assets(int number, string name, AssetLine expected) =>
        Assert.Equal(expected, StatementClassifier.ClassifyAsset(number, name));

    [Theory]
    [InlineData(2900, "Trade Clearing / Suspense", LiabilityLine.Clearing)]
    [InlineData(9999, "Bank Import Suspense", LiabilityLine.Clearing)]
    [InlineData(2100, "Income tax payable", LiabilityLine.TaxPayable)]
    [InlineData(2200, "Beneficiary entitlements", LiabilityLine.DistributionsPayable)]
    [InlineData(2600, "Margin loan", LiabilityLine.Borrowings)]
    [InlineData(2050, "Accrued expenses", LiabilityLine.Payables)]
    [InlineData(2300, "Sundry", LiabilityLine.Payables)]
    [InlineData(2700, "Sundry", LiabilityLine.Borrowings)]
    [InlineData(8000, "Sundry", LiabilityLine.Other)]
    public void Liabilities(int number, string name, LiabilityLine expected) =>
        Assert.Equal(expected, StatementClassifier.ClassifyLiability(number, name));

    [Theory]
    [InlineData(4100, "Interest Income", RevenueLine.Interest)]
    [InlineData(4200, "Dividend Income", RevenueLine.Dividends)]
    [InlineData(4300, "Trust Distribution Income", RevenueLine.TrustDistributions)]
    [InlineData(4400, "Rental Income", RevenueLine.Rent)]
    [InlineData(4900, "Other Investment Income", RevenueLine.Other)]
    [InlineData(4500, "Gain on sale of shares", RevenueLine.Gains)]
    [InlineData(4600, "Employer contributions", RevenueLine.Contributions)]
    public void Revenue(int number, string name, RevenueLine expected) =>
        Assert.Equal(expected, StatementClassifier.ClassifyRevenue(number, name));

    [Theory]
    [InlineData(3100, "Owner's Capital", EquityKind.Capital)]
    [InlineData(3000, "Share Capital", EquityKind.Capital)]
    [InlineData(3010, "Settled sum", EquityKind.Capital)]
    [InlineData(3200, "Retained Earnings", EquityKind.Retained)]
    [InlineData(3210, "Undistributed income", EquityKind.Retained)]
    [InlineData(3300, "Dividends Paid", EquityKind.Distributions)]
    [InlineData(3310, "Distributions to beneficiaries", EquityKind.Distributions)]
    [InlineData(3320, "Drawings", EquityKind.Distributions)]
    [InlineData(3330, "Benefits paid", EquityKind.Distributions)]
    [InlineData(3400, "Member - Jane Smith", EquityKind.MemberBenefits)]
    [InlineData(3150, "Partner A current account", EquityKind.CurrentAccount)]
    [InlineData(3250, "Sundry", EquityKind.Retained)]
    [InlineData(3350, "Sundry", EquityKind.Distributions)]
    [InlineData(3900, "Asset revaluation", EquityKind.Other)]
    [InlineData(null, "Opening balance equity", EquityKind.Other)]
    public void Equity(int? number, string name, EquityKind expected) =>
        Assert.Equal(expected, StatementClassifier.ClassifyEquity(number, name));

    [Fact]
    public void Income_tax_expense_is_separate()
    {
        Assert.Equal(ExpenseLine.IncomeTax, StatementClassifier.ClassifyExpense(5900, "Income Tax Expense"));
        Assert.Equal(ExpenseLine.Operating, StatementClassifier.ClassifyExpense(5400, "Rental Property Expenses"));
    }

    [Fact]
    public void Cost_base_suffix_is_dropped() =>
        Assert.Equal("BHP.ASX", StatementClassifier.InvestmentName("BHP.ASX - Cost Base"));

    [Theory]
    [InlineData(1234.5, "1,234.50")]
    [InlineData(-20, "(20.00)")]
    [InlineData(0, "-")]
    public void Amounts_are_in_accounting_style(decimal value, string expected) =>
        Assert.Equal(expected, StatementFormat.Amount(value));
}

public class FinancialStatementsBuilderTests
{
    // Account keys
    private static readonly (int, string, string) Bank = (1101, "Westpac", "Asset");
    private static readonly (int, string, string) Bhp = (1301, "BHP.ASX - Cost Base", "Asset");
    private static readonly (int, string, string) Cba = (1301, "CBA.ASX - Cost Base", "Asset");
    private static readonly (int, string, string) Franking = (1410, "Franking Credits Receivable", "Asset");
    private static readonly (int, string, string) Clearing = (2900, "Trade Clearing / Suspense", "Liability");
    private static readonly (int, string, string) Capital = (3000, "Share Capital", "Equity");
    private static readonly (int, string, string) Dividends = (4200, "Dividend Income", "Revenue");
    private static readonly (int, string, string) Interest = (4100, "Interest Income", "Revenue");
    private static readonly (int, string, string) Fees = (5100, "Accounting fees", "Expense");
    private static readonly (int, string, string) TaxExpense = (5900, "Income Tax Expense", "Expense");

    /// <summary>Builds a trial balance at a date from cumulative journals (account, debit, credit).</summary>
    private sealed class Ledger
    {
        private readonly Dictionary<(int?, string, string), (decimal D, decimal C)> _totals = [];

        public Ledger Post(params ((int, string, string) Account, decimal Debit, decimal Credit)[] lines)
        {
            foreach (var (account, debit, credit) in lines)
            {
                var key = ((int?)account.Item1, account.Item2, account.Item3);
                var t = _totals.GetValueOrDefault(key);
                _totals[key] = (t.D + debit, t.C + credit);
            }
            return this;
        }

        public LedgerSnapshot At(int year) => new(
            new DateOnly(year, 6, 30),
            _totals.Select(kv => new LedgerBalance(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Value.D, kv.Value.C)),
            _totals.Values.Sum(v => v.D),
            _totals.Values.Sum(v => v.C));
    }

    private static ClientDetails Client(string type, bool corporateTrustee = false) => new(
        1, "Test", "Company", true, "Smith Pty Ltd", type, "53004085616", "004085616",
        corporateTrustee ? "Smith Nominees Pty Ltd" : null, corporateTrustee ? TrusteeTypes.Corporate : null, null,
        new PostalAddress(null, null, null, null, null), ClientDetails.DefaultReportingBasis, null, []);

    /// <summary>Three years of a small investment company (figures worked by hand in the comments).</summary>
    private static (LedgerSnapshot, LedgerSnapshot, LedgerSnapshot) CompanyLedger(string capitalName = "Share Capital", string paidName = "Dividends Paid")
    {
        var capital = (3000, capitalName, "Equity");
        var paid = (3300, paidName, "Equity");
        var l = new Ledger();
        // To 30 June 2024: capital 10,000, BHP 6,000, dividend 1,000 with 300 franking, fees 200
        l.Post((Bank, 10000, 0), (capital, 0, 10000))
         .Post((Bhp, 6000, 0), (Bank, 0, 6000))
         .Post((Bank, 700, 0), (Franking, 300, 0), (Dividends, 0, 1000))
         .Post((Fees, 200, 0), (Bank, 0, 200));
        var s0 = l.At(2024);
        // FY2025: dividend 2,000, interest 50, fees 300, dividends paid 500, tax 150: profit 1,600
        l.Post((Bank, 1400, 0), (Franking, 600, 0), (Dividends, 0, 2000))
         .Post((Bank, 50, 0), (Interest, 0, 50))
         .Post((Fees, 300, 0), (Bank, 0, 300))
         .Post((paid, 500, 0), (Bank, 0, 500))
         .Post((TaxExpense, 150, 0), (Bank, 0, 150));
        var s1 = l.At(2025);
        // FY2026: dividend 3,000, fees 400, unsettled CBA buy 1,000, dividends paid 1,000: profit 2,600
        l.Post((Bank, 2100, 0), (Franking, 900, 0), (Dividends, 0, 3000))
         .Post((Fees, 400, 0), (Bank, 0, 400))
         .Post((Cba, 1000, 0), (Clearing, 0, 1000))
         .Post((paid, 1000, 0), (Bank, 0, 1000));
        return (s0, s1, l.At(2026));
    }

    private static StatementRow Row(Statement s, string label) => s.Rows.Single(r => r.Label == label);
    private static StatementNote Note(FinancialStatements fs, string title) => fs.Notes.Single(n => n.Title == title);

    [Fact]
    public void Profit_is_the_movement_in_each_year_with_comparatives()
    {
        var (s0, s1, s2) = CompanyLedger();
        var fs = FinancialStatementsBuilder.Build(Client(ClientEntityTypes.Company), 2026, s0, s1, s2);

        Assert.Equal(2600, fs.Profit);
        Assert.Equal(1600, fs.PriorProfit);
        var dividends = Row(fs.IncomeStatement, "Dividend income (including franking credits)");
        Assert.Equal(3000, dividends.Current);
        Assert.Equal(2000, dividends.Prior);
        var interest = Row(fs.IncomeStatement, "Interest income");
        Assert.Equal(0, interest.Current);
        Assert.Equal(50, interest.Prior);
        var tax = Row(fs.IncomeStatement, "Income tax expense");
        Assert.Equal(0, tax.Current);
        Assert.Equal(150, tax.Prior);
        Assert.Equal(1750, Row(fs.IncomeStatement, "Profit/(loss) before income tax").Prior);
        Assert.Equal("Detailed profit and loss statement", fs.IncomeStatement.Title);
    }

    [Fact]
    public void The_balance_sheet_reconciles_and_rolls_profit_into_equity()
    {
        var (s0, s1, s2) = CompanyLedger();
        var fs = FinancialStatementsBuilder.Build(Client(ClientEntityTypes.Company), 2026, s0, s1, s2);

        Assert.Equal(new PositionTotals(14500, 1000, 13500), fs.Position);
        Assert.Equal(new PositionTotals(11900, 0, 11900), fs.PriorPosition);
        Assert.True(fs.Reconciles);
        Assert.Equal(7000, Row(fs.FinancialPosition, "Investments").Current);
        Assert.Equal(10000, Row(fs.FinancialPosition, "Issued capital").Current);
        var retained = Row(fs.FinancialPosition, "Retained earnings");
        Assert.Equal(3500, retained.Current);
        Assert.Equal(1900, retained.Prior);
        Assert.Equal(13500, Row(fs.FinancialPosition, "Total equity").Current);
    }

    [Fact]
    public void Company_retained_earnings_note_shows_dividends_paid()
    {
        var (s0, s1, s2) = CompanyLedger();
        var fs = FinancialStatementsBuilder.Build(Client(ClientEntityTypes.Company), 2026, s0, s1, s2);

        var rows = Note(fs, "Retained earnings").Tables.Single().Rows;
        Assert.Equal((1900m, 800m), (rows[0].Current!.Value, rows[0].Prior!.Value));
        var paid = rows.Single(r => r.Label == "Dividends paid");
        Assert.Equal((-1000m, -500m), (paid.Current!.Value, paid.Prior!.Value));
        Assert.DoesNotContain(rows, r => r.Label == "Other movements");
        Assert.Equal(3500, rows[^1].Current);
    }

    [Fact]
    public void Notes_are_numbered_and_referenced()
    {
        var (s0, s1, s2) = CompanyLedger();
        var fs = FinancialStatementsBuilder.Build(Client(ClientEntityTypes.Company), 2026, s0, s1, s2);

        Assert.Equal("Summary of significant accounting policies", fs.Notes[0].Title);
        Assert.Equal(Enumerable.Range(1, fs.Notes.Count), fs.Notes.Select(n => n.Number));
        var investments = Row(fs.FinancialPosition, "Investments");
        var note = fs.Notes.Single(n => n.Number == investments.Note);
        Assert.Contains(note.Tables.Single().Rows, r => r.Label == "CBA.ASX" && r.Current == 1000 && r.Prior == 0);
        Assert.Equal(Note(fs, "Income tax").Number, Row(fs.FinancialPosition, "Tax credits receivable").Note);
        Assert.Contains(fs.Notes[0].Paragraphs, p => p.Contains("the directors have prepared", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_clearing_balance_is_flagged()
    {
        var (s0, s1, s2) = CompanyLedger();
        var fs = FinancialStatementsBuilder.Build(Client(ClientEntityTypes.Company), 2026, s0, s1, s2);

        Assert.Equal(1000, Row(fs.FinancialPosition, "Unsettled trades and suspense accounts").Current);
        Assert.Contains(fs.Warnings, w => w.StartsWith("Trade Clearing / Suspense"));
    }

    [Fact]
    public void Trust_equity_uses_undistributed_income_and_distributions()
    {
        var (s0, s1, s2) = CompanyLedger("Settled sum", "Distributions to beneficiaries");
        var fs = FinancialStatementsBuilder.Build(Client(ClientEntityTypes.Trust, corporateTrustee: true), 2026, s0, s1, s2);

        Assert.Equal(10000, Row(fs.FinancialPosition, "Settled sum and unitholders' capital").Current);
        Assert.Equal(3500, Row(fs.FinancialPosition, "Undistributed income").Current);
        Assert.Contains(Note(fs, "Undistributed income").Tables.Single().Rows, r => r.Label == "Distributions to beneficiaries" && r.Current == -1000);
        Assert.Contains(fs.Notes[0].Paragraphs, p => p.Contains("directors of the trustee company have prepared"));
        Assert.True(fs.Reconciles);
    }

    [Fact]
    public void Smsf_shows_net_assets_available_to_pay_benefits_and_member_balances()
    {
        var l = new Ledger();
        var member = (3400, "Member - Jane Smith", "Equity");
        var contributions = (4600, "Employer contributions", "Revenue");
        var benefits = (3300, "Benefits paid", "Equity");
        l.Post((Bank, 50000, 0), (member, 0, 50000));
        var s0 = l.At(2024);
        var s1 = l.At(2025);
        l.Post((Bank, 10000, 0), (contributions, 0, 10000))
         .Post((Bank, 2000, 0), (Dividends, 0, 2000))
         .Post((benefits, 3000, 0), (Bank, 0, 3000));
        var fs = FinancialStatementsBuilder.Build(Client(ClientEntityTypes.Smsf), 2026, s0, s1, l.At(2026));

        Assert.Equal("Operating statement", fs.IncomeStatement.Title);
        Assert.Contains(fs.Notes[0].Paragraphs, p => p.Contains("The trustees have prepared"));
        Assert.Equal(12000, Row(fs.IncomeStatement, "Benefits accrued as a result of operations").Current);
        Assert.Equal(59000, Row(fs.FinancialPosition, "Net assets available to pay benefits").Current);
        Assert.Equal(50000, Row(fs.FinancialPosition, "Member - Jane Smith").Current);
        Assert.Equal(9000, Row(fs.FinancialPosition, "Benefits accrued not yet allocated to members").Current);
        Assert.Equal(59000, Row(fs.FinancialPosition, "Total liability for accrued benefits").Current);
        var rows = Note(fs, "Liability for accrued benefits").Tables.Single().Rows;
        Assert.Contains(rows, r => r.Label == "Benefits paid" && r.Current == -3000);
        Assert.True(fs.Reconciles);
    }

    [Fact]
    public void Partnership_shows_capital_and_current_accounts()
    {
        var l = new Ledger();
        var capA = (3000, "Partner A capital", "Equity");
        var drawings = (3300, "Partner A drawings", "Equity");
        l.Post((Bank, 5000, 0), (capA, 0, 5000));
        var s0 = l.At(2024);
        var s1 = l.At(2025);
        l.Post((Bank, 1000, 0), (Interest, 0, 1000)).Post((drawings, 400, 0), (Bank, 0, 400));
        var fs = FinancialStatementsBuilder.Build(Client(ClientEntityTypes.Partnership), 2026, s0, s1, l.At(2026));

        Assert.Equal(5000, Row(fs.FinancialPosition, "Partners' capital accounts").Current);
        Assert.Equal(600, Row(fs.FinancialPosition, "Partners' current accounts").Current);
        Assert.Contains(Note(fs, "Partners' current accounts").Tables.Single().Rows, r => r.Label == "Partners' drawings" && r.Current == -400);
        Assert.Equal("Partners' funds", fs.FinancialPosition.Blocks[^1].Heading);
        Assert.True(fs.Reconciles);
    }

    [Fact]
    public void Individual_capital_account_splits_money_in_and_out()
    {
        var l = new Ledger();
        var owner = (3100, "Owner's Capital", "Equity");
        l.Post((Bank, 1000, 0), (owner, 0, 1000));
        var s0 = l.At(2024);
        l.Post((Bank, 4000, 0), (owner, 0, 4000)).Post((owner, 1500, 0), (Bank, 0, 1500)).Post((Bank, 200, 0), (Interest, 0, 200));
        var s1 = l.At(2025);
        var fs = FinancialStatementsBuilder.Build(Client(ClientEntityTypes.Individual), 2025, s0, s0, s1);

        Assert.Equal(3500, Row(fs.FinancialPosition, "Capital account").Current);
        Assert.Equal(200, Row(fs.FinancialPosition, "Retained profits").Current);
        var rows = Note(fs, "Owner's equity").Tables.Single().Rows;
        Assert.Contains(rows, r => r.Label == "Capital introduced" && r.Current == 4000);
        Assert.Contains(rows, r => r.Label == "Drawings" && r.Current == -1500);
        Assert.DoesNotContain(rows, r => r.Label == "Other movements");
        Assert.Equal(3700, rows[^1].Current);
        Assert.Equal("Total owner's equity", fs.FinancialPosition.Rows.Last().Label);
    }

    [Fact]
    public void Unclassified_equity_is_shown_as_other_not_dropped()
    {
        var l = new Ledger();
        var odd = (3900, "Opening balance equity", "Equity");
        l.Post((Bank, 700, 0), (odd, 0, 700));
        var s = l.At(2026);
        var fs = FinancialStatementsBuilder.Build(Client(ClientEntityTypes.Company), 2026, s, s, s);

        Assert.Equal(700, Row(fs.FinancialPosition, "Other equity").Current);
        Assert.Contains(fs.Warnings, w => w.Contains("3900 Opening balance equity"));
        Assert.True(fs.Reconciles);
    }

    [Fact]
    public void An_unbalanced_ledger_is_flagged()
    {
        var l = new Ledger();
        l.Post((Bank, 100, 0), (Dividends, 0, 90));
        var s = l.At(2026);
        var fs = FinancialStatementsBuilder.Build(Client(ClientEntityTypes.Company), 2026, s, s, s);

        Assert.False(fs.Reconciles);
        Assert.Equal(10, fs.Position.Difference);
        Assert.Contains(fs.Warnings, w => w.Contains("differ by $10.00"));
    }

    [Fact]
    public void Credit_balances_in_investments_are_flagged_but_sales_are_not()
    {
        var l = new Ledger();
        l.Post((Bank, 1000, 0), (Capital, 0, 1000)).Post((Bhp, 1000, 0), (Bank, 0, 1000));
        var s1 = l.At(2025);
        l.Post((Bank, 1300, 0), (Bhp, 0, 1300));
        var fs = FinancialStatementsBuilder.Build(Client(ClientEntityTypes.Company), 2026, s1, s1, l.At(2026));

        Assert.Contains(fs.Warnings, w => w.Contains("credit balance of $300.00"));
        // Sales post their own realised gain or loss now, so they need no manual journal
        Assert.DoesNotContain(fs.Warnings, w => w.Contains("sale proceeds"));
    }

    [Fact]
    public void Unsaved_client_details_and_single_entry_are_flagged()
    {
        var s = new Ledger().At(2026);
        var client = ClientDetails.Unsaved(1, "Test", "Individual");
        var fs = FinancialStatementsBuilder.Build(client, 2026, s, s, s, "SingleEntry");

        Assert.True(fs.IsEmpty);
        Assert.Contains(fs.Warnings, w => w.Contains("haven't been saved"));
        Assert.Contains(fs.Warnings, w => w.Contains("single-entry"));
    }

    [Fact]
    public void Trial_balance_rows_become_balances()
    {
        var report = LedgerReportService.MapTrialBalance(
        [
            new Dictionary<string, object?> { ["GroupName"] = "Income", ["AccountName"] = "INCOME", ["RowType"] = "Header" },
            new Dictionary<string, object?> { ["GroupName"] = "Income", ["AccountNumber"] = 4200, ["AccountName"] = "Dividend Income", ["Debit"] = 0m, ["Credit"] = 500m, ["RowType"] = "Item" },
            new Dictionary<string, object?> { ["GroupName"] = "CurrAsset", ["AccountNumber"] = 1101, ["AccountName"] = "Westpac", ["Debit"] = 600m, ["Credit"] = 100m, ["RowType"] = "Item" },
            new Dictionary<string, object?> { ["GroupName"] = "NetProfit", ["AccountName"] = "NET PROFIT / (LOSS)", ["Debit"] = 0m, ["Credit"] = 500m, ["RowType"] = "GrandTotal" },
            new Dictionary<string, object?> { ["GroupName"] = "Footer", ["AccountName"] = "TRIAL BALANCE TOTAL", ["Debit"] = 650m, ["Credit"] = 600m, ["RowType"] = "GrandTotal" },
        ]);
        var s = LedgerSnapshot.FromTrialBalance(new DateOnly(2026, 6, 30), report);

        Assert.Equal(2, s.Accounts.Count);
        Assert.Equal(500, s.Balance((4200, "Dividend Income", "Revenue")));
        Assert.Equal(500, s.Balance((1101, "Westpac", "Asset")));
        Assert.Equal(500, s.CumulativeProfit);
        // A 50 debit sits in an account of an unknown type: in the footer but not in any group
        Assert.Equal(50, s.UngroupedDifference);
    }
}
