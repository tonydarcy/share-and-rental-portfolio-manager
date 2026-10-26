using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Debt;
using PortfolioManager.Core.Statements;
using PortfolioManager.Core.TaxWork;
using static PortfolioManager.Tests.TaxReconciliationTests;

namespace PortfolioManager.Tests;

public class LoansInLedgerTests
{
    private static readonly Loan Margin = new(1, "Margin loan", null, LoanKinds.Margin, 8m, 80m, null, new(2025, 7, 1), null);
    private static readonly Loan Rental = new(2, "Rental loan", null, LoanKinds.Investment, 6m, 100m, null, new(2025, 7, 1), null);

    private static readonly LoanEntry[] Entries =
    [
        new(1, 1, new(2025, 7, 1), LoanEntryKinds.Drawdown, 10_000, null),
        new(2, 1, new(2026, 5, 31), LoanEntryKinds.Interest, 500, null),
        new(3, 2, new(2025, 7, 1), LoanEntryKinds.Drawdown, 20_000, null),
        new(4, 2, new(2026, 6, 30), LoanEntryKinds.Interest, 1_000, null),
    ];

    private static readonly Dictionary<int, int> RentalLinks = new() { [2] = 7 };

    [Fact]
    public void Splits_the_years_interest_into_investment_and_rental_loans()
    {
        var y = LoanInterestYear.For([Margin, Rental], Entries, RentalLinks, 2026);
        Assert.Equal((1_500m, 400m, 1_000m), (y.Charged, y.Deductible, y.RentalDeductible));
    }

    [Fact]
    public void With_the_interest_in_the_accounts_the_reconciliation_adds_back_what_isnt_deducted_here()
    {
        var loans = LoanInterestYear.For([Margin, Rental], Entries, RentalLinks, 2026);
        var ledger = Year(Revenue(4200, "Dividend Income", 5_000), Expense(5300, "Investment Loan Interest", 1_500));
        var rec = TaxReconciliation.Build(ClientEntityTypes.Individual, ledger, Gains(HalfRate), TaxIncomeFacts.None, TaxYearData.Empty, loans: loans);

        // Profit 3,500, plus the 1,100 that's private (100) or claimed in the rental schedule (1,000)
        Assert.Contains(rec.Lines, l => l.Label.StartsWith("Add: loan interest") && l.Amount == 1_100m);
        Assert.Equal(4_600m, rec.TaxableIncome);
    }

    [Fact]
    public void Without_the_interest_in_the_accounts_the_deductible_part_comes_off()
    {
        var loans = LoanInterestYear.For([Margin], Entries, new Dictionary<int, int>(), 2026);
        var rec = TaxReconciliation.Build(ClientEntityTypes.Individual, Year(Revenue(4200, "Dividend Income", 5_000)), Gains(HalfRate),
            TaxIncomeFacts.None, TaxYearData.Empty, loans: loans);
        Assert.Contains(rec.Lines, l => l.Label.StartsWith("Less: deductible interest") && l.Amount == -400m);
        Assert.Equal(4_600m, rec.TaxableIncome);
    }

    [Fact]
    public void The_deductible_loan_interest_is_a_dividend_deduction_not_an_interest_deduction()
    {
        var loans = LoanInterestYear.For([Margin], Entries, new Dictionary<int, int>(), 2026);
        var ledger = Year(Revenue(4200, "Dividend Income", 5_000), Expense(5300, "Investment Loan Interest", 500), Expense(6010, "Bank interest charges", 30),
            Expense(6000, "Accounting Fees", 200));
        var w = TaxWorkpaperService.Assemble(ClientDetails.Unsaved(1, "Jo", ClientEntityTypes.Individual), 2026, ledger, TaxYearData.Empty,
            new(Gains(HalfRate), [], true, []), TaxIncomeFacts.None, loans: loans);
        var labels = TaxReturnSchedule.For(w).Labels.ToDictionary(l => l.Code, l => l.Amount);
        Assert.Equal(30m, labels["D7"]);
        Assert.Equal(600m, labels["D8"]); // accounting fees 200 plus 80% of the 500
    }

    [Fact]
    public void Interest_accrued_at_the_year_end_comes_out_of_taxable_income()
    {
        var ledger = new LedgerYear(
            new LedgerSnapshot(new(2025, 6, 30), [new(1450, "Accrued Interest Receivable", "Asset", 40, 0)], 0, 0),
            new LedgerSnapshot(new(2026, 6, 30), [new(1450, "Accrued Interest Receivable", "Asset", 100, 0), Revenue(4100, "Interest Income", 1_000)], 0, 0));
        var rec = TaxReconciliation.Build(ClientEntityTypes.Company, ledger, Gains(CompanyRate), TaxIncomeFacts.None, TaxYearData.Empty);
        Assert.Contains(rec.Lines, l => l.Label.StartsWith("Less: interest accrued") && l.Amount == -60m);
    }

    [Fact]
    public void Loan_interest_accrues_from_when_it_was_last_charged()
    {
        // 10,500 (with the interest charged) at 8% from 31 May to 30 June: 30 days
        Assert.Equal(69.04m, LoanCalc.AccruedInterest(Margin, Entries, new(2026, 6, 30)));
        Assert.Equal(0m, LoanCalc.AccruedInterest(Rental, Entries, new(2026, 6, 30)));
        Assert.Equal(0m, LoanCalc.AccruedInterest(Margin with { RatePercent = 0 }, Entries, new(2026, 6, 30)));
    }

    [Fact]
    public void The_year_end_accruals_cover_fixed_income_and_loans()
    {
        var td = new FixedIncomeHolding(1, FixedIncomeKinds.TermDeposit, "Bank TD", null, null, 36_500, 4m, new(2026, 1, 1), new(2026, 12, 31),
            InterestFrequency.AtMaturity, null);
        var acc = InterestAccruals.Build(2026, [td], [Margin, Rental], Entries);
        Assert.Equal(720m, acc.ReceivableTotal); // 180 days at $4 a day
        Assert.Equal(69.04m, acc.PayableTotal);
        var p = acc.Parameters(3);
        object? Get(string name) => p.GetType().GetProperty(name)!.GetValue(p);
        Assert.Equal((3, 2026, new DateTime(2026, 6, 30), 720m, 69.04m),
            ((int)Get("iUserID")!, (int)Get("iYear")!, (DateTime)Get("dtmYearEnd")!, (decimal)Get("dblReceivable")!, (decimal)Get("dblPayable")!));
    }
}
