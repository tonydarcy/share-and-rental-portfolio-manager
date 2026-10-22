using PortfolioManager.Core.Payouts;

namespace PortfolioManager.Tests;

public class RentalPayoutTests
{
    private static int Index(string column) =>
        RentalPayoutInput.Fields.Select((f, i) => (f, i)).Single(x => x.f.Column == column).i;

    private static RentalPayoutInput Sample()
    {
        var r = new RentalPayoutInput { PropertyCode = " 12smithst ", ExchangeCode = "prop", Date = new DateTime(2026, 3, 31, 14, 0, 0), Comment = "  March statement " };
        r.Amounts[Index("dblRentalIncomeAmount")] = 2000m;
        r.Amounts[Index("dblPropertyAgentFeesAndCommissionExpenseAmount")] = 154m;
        r.Amounts[Index("dblRepairsAndMaintenanceExpenseAmount")] = 220.5m;
        return r;
    }

    [Fact]
    public void Has_all_thirty_proc_amounts_two_of_them_income()
    {
        Assert.Equal(30, RentalPayoutInput.Fields.Count);
        Assert.Equal(2, RentalPayoutInput.Fields.Count(f => f.IsIncome));
        Assert.Equal(30, RentalPayoutInput.Fields.Select(f => f.Column).Distinct().Count());
        Assert.All(RentalPayoutInput.Fields, f => Assert.StartsWith("dbl", f.Column));
    }

    [Fact]
    public void Net_is_income_less_expenses()
    {
        var r = Sample();
        Assert.Equal(2000m, r.Income);
        Assert.Equal(374.5m, r.Expenses);
        Assert.Equal(1625.5m, r.Net);
    }

    [Fact]
    public void Net_can_be_negative_when_expenses_are_larger()
    {
        var r = new RentalPayoutInput { PropertyCode = "UNIT" };
        r.Amounts[Index("dblCouncilRatesExpenseAmount")] = 600m;
        Assert.Equal(-600m, r.Net);
        Assert.Empty(r.Validate());
    }

    [Fact]
    public void Add_parameters_cover_every_proc_parameter()
    {
        var p = Sample().AddParameters(7);
        Assert.Equal(7, p["iUserId"]);
        Assert.Equal("12SMITHST", p["strStockCode"]);
        Assert.Equal("PROP", p["strStockExchangeCode"]);
        Assert.Equal(new DateTime(2026, 3, 31), p["dtmTransactionDate"]);
        Assert.Equal("March statement", p["strComment"]);
        Assert.Equal(2000d, p["dblRentalIncomeAmount"]);
        Assert.Equal(0d, p["dblSundryRentalExpense10Amount"]);
        Assert.Equal(5 + 30, p.Count);
        Assert.False(p.ContainsKey("iPayoutID"));
    }

    [Fact]
    public void Update_parameters_add_the_payout_id()
    {
        var p = Sample().UpdateParameters(7, 42);
        Assert.Equal(42, p["iPayoutID"]);
        Assert.Equal(5 + 30 + 1, p.Count);
    }

    [Fact]
    public void A_short_amounts_array_counts_missing_amounts_as_zero()
    {
        var r = new RentalPayoutInput { PropertyCode = "UNIT", Amounts = [1000m] };
        Assert.Equal(1000m, r.Net);
        Assert.Equal(0d, r.AddParameters(1)["dblWaterChargesExpenseAmount"]);
    }

    [Theory]
    [InlineData("", "PROP", 100, "property")]
    [InlineData("UNIT", "", 100, "exchange")]
    [InlineData("UNIT", "PROP", -5, "negative")]
    [InlineData("UNIT", "PROP", 0, "rent received")]
    public void Validation_catches_bad_input(string code, string exchange, int rent, string expected)
    {
        var r = new RentalPayoutInput { PropertyCode = code, ExchangeCode = exchange };
        r.Amounts[0] = rent;
        Assert.Contains(r.Validate(), e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Reads_a_stored_row_back()
    {
        var row = new Dictionary<string, object?>
        {
            ["strStockExchangeCode"] = "PROP ",
            ["strStockCode"] = "12SMITHST",
            ["dtmTransactionDate"] = new DateTime(2026, 3, 31),
            ["strComment"] = DBNull.Value,
            ["dblRentalIncomeAmount"] = 2000.004d,
            ["dblRepairsAndMaintenanceExpenseAmount"] = 220.5d,
            ["dblWaterChargesExpenseAmount"] = DBNull.Value,
        };
        var r = RentalPayoutInput.FromRow(row);
        Assert.Equal("PROP", r.ExchangeCode);
        Assert.Equal("12SMITHST", r.PropertyCode);
        Assert.Null(r.Comment);
        Assert.Equal(2000m, r.Amount(Index("dblRentalIncomeAmount")));
        Assert.Equal(220.5m, r.Amount(Index("dblRepairsAndMaintenanceExpenseAmount")));
        Assert.Equal(1779.5m, r.Net);
    }
}
