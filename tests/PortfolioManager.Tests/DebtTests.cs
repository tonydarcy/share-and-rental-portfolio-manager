using PortfolioManager.Core.Debt;

namespace PortfolioManager.Tests;

public class DebtTests
{
    private static readonly DateOnly From = new(2025, 7, 1), To = new(2026, 6, 30);

    private static readonly Loan Margin = new(1, "Margin", "Bank", LoanKinds.Margin, 8.5m, 80m, 50000m, new(2024, 1, 1), null);

    private static readonly LoanEntry[] Entries =
    [
        new(1, 1, new(2024, 2, 1), LoanEntryKinds.Drawdown, 10000m, null),
        new(2, 1, new(2025, 8, 1), LoanEntryKinds.Drawdown, 5000m, null),
        new(3, 1, new(2025, 12, 31), LoanEntryKinds.Interest, 600m, null),
        new(4, 1, new(2026, 3, 1), LoanEntryKinds.Repayment, 2000m, null),
        new(5, 1, new(2026, 6, 30), LoanEntryKinds.Fee, 100m, null),
        new(6, 2, new(2026, 1, 1), LoanEntryKinds.Drawdown, 999m, null),   // another loan
    ];

    [Fact]
    public void A_loan_year_has_the_balances_and_the_deductible_part()
    {
        var y = LoanCalc.Year(Margin, Entries, From, To);
        Assert.Equal((10000m, 5000m, 2000m, 600m, 100m, 13700m), (y.OpeningBalance, y.Drawdowns, y.Repayments, y.Interest, y.Fees, y.ClosingBalance));
        Assert.Equal(560m, y.Deductible);
    }

    [Fact]
    public void Loan_to_value_is_owed_over_market_value()
    {
        var r = new LoanReport([LoanCalc.Year(Margin, Entries, From, To)], 54800m);
        Assert.Equal(0.25m, r.LoanToValue);
        Assert.Null(new LoanReport([], 0).LoanToValue);
        Assert.NotEmpty(LoanCalc.Validate(Margin with { DeductiblePercent = 120 }));
        Assert.Empty(LoanCalc.Validate(Margin));
    }

    [Fact]
    public void A_term_deposit_earns_by_the_day_and_accrues_until_maturity()
    {
        var td = new FixedIncomeHolding(1, FixedIncomeKinds.TermDeposit, "TD", null, null, 50000m, 5m, new(2026, 1, 1), new(2027, 1, 1), InterestFrequency.AtMaturity, null);
        var y = FixedIncomeCalc.Year(td, From, To);
        Assert.Equal((1232.88m, 1232.88m), (y.Earned, y.AccruedAtEnd));   // 180 days
        Assert.Equal(new DateOnly(2027, 1, 1), y.NextPayment);

        var next = FixedIncomeCalc.Year(td, new(2026, 7, 1), new(2027, 6, 30));
        Assert.Equal(0m, next.AccruedAtEnd);   // matured and paid
        Assert.Equal(50000m * 0.05m * 185 / 365, next.Earned, 2);
    }

    [Fact]
    public void A_bond_accrues_from_its_last_coupon()
    {
        // Quarterly coupons back from 15 Aug 2030: 15 May, 15 Aug, 15 Nov, 15 Feb
        var bond = new FixedIncomeHolding(2, FixedIncomeKinds.Bond, "Bond", "ASX", "XYZHA", 10000m, 6m, new(2025, 8, 15), new(2030, 8, 15), InterestFrequency.Quarterly, null);
        Assert.Equal(new DateOnly(2026, 5, 15), FixedIncomeCalc.LastPaymentOnOrBefore(bond, To));
        var y = FixedIncomeCalc.Year(bond, From, To);
        Assert.Equal(Math.Round(10000m * 0.06m * 46 / 365, 2), y.AccruedAtEnd);
        Assert.Equal(new DateOnly(2026, 8, 15), y.NextPayment);
    }

    [Fact]
    public void Checks_catch_missing_dates_and_amounts()
    {
        var bad = new FixedIncomeHolding(0, "TermDeposit", "", null, null, 0, 5, new(2026, 1, 1), null, InterestFrequency.AtMaturity, null);
        var errors = FixedIncomeCalc.Validate(bad);
        Assert.Contains(errors, e => e.Contains("name"));
        Assert.Contains(errors, e => e.Contains("face value"));
        Assert.Contains(errors, e => e.Contains("maturity date"));
    }
}
