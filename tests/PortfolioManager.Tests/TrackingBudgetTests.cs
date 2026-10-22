using PortfolioManager.Core.Accounting;

namespace PortfolioManager.Tests;

public class TrackingBudgetTests
{
    [Fact]
    public void Spread_puts_the_rounding_in_the_last_month()
    {
        var months = BudgetRules.Spread(1_000);
        Assert.Equal(83.33m, months[0]);
        Assert.Equal(83.37m, months[11]);
        Assert.Equal(1_000m, months.Sum());
    }

    [Fact]
    public void Months_follow_the_financial_year()
    {
        var months = BudgetRules.Months(2027);
        Assert.Equal(new DateOnly(2026, 7, 1), months[0]);
        Assert.Equal(new DateOnly(2027, 6, 1), months[11]);
    }

    [Fact]
    public void More_income_or_less_spending_is_better()
    {
        var income = new BudgetLine(1, 4000, "Sales", "Revenue", new decimal[12], new decimal[12]);
        var cost = income with { Type = "Expense" };
        Assert.Equal(20m, income.Variance(100, 120));
        Assert.Equal(-20m, cost.Variance(100, 120));
    }

    [Fact]
    public void Budget_xml_leaves_out_empty_months()
    {
        var months = new decimal[12];
        months[0] = 50;
        months[11] = 12.5m;
        Assert.Equal("<lines><b a=\"9\" m=\"1\" v=\"50\" /><b a=\"9\" m=\"12\" v=\"12.5\" /></lines>",
            BudgetRules.LinesXml(new Dictionary<int, decimal[]> { [9] = months }));
    }
}
