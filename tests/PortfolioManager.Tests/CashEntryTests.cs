using PortfolioManager.Core.Cash;
using PortfolioManager.Core.Validation;

namespace PortfolioManager.Tests;

public class CashEntryTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static CashForm Valid() => new()
    {
        Date = new DateOnly(2026, 10, 1),
        Direction = "Deposit",
        Amount = 5000m,
    };

    private static IEnumerable<string> Fields(Validated<NewCashTransaction> r) => r.Errors.Select(e => e.Field);

    [Fact]
    public void Deposit_maps_to_the_deposit_column_only()
    {
        var c = CashEntry.Validate(Valid(), Today).Value!;
        Assert.Equal(5000m, c.Deposit);
        Assert.Equal(0m, c.Withdrawal);
    }

    [Fact]
    public void Withdrawal_maps_to_the_withdrawal_column_only()
    {
        var f = Valid();
        f.Direction = "withdrawal";
        var c = CashEntry.Validate(f, Today).Value!;
        Assert.Equal(0m, c.Deposit);
        Assert.Equal(5000m, c.Withdrawal);
    }

    [Fact]
    public void Required_fields_are_reported()
    {
        var r = CashEntry.Validate(new CashForm(), Today);
        Assert.Equivalent(new[] { "Date", "Direction", "Amount" }, Fields(r).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(10.005)]
    [InlineData(100_000_000)]
    public void Amount_must_be_positive_cents_that_fit_the_column(double amount)
    {
        var f = Valid();
        f.Amount = (decimal)amount;
        Assert.Contains(nameof(CashForm.Amount), Fields(CashEntry.Validate(f, Today)));
    }

    [Fact]
    public void Future_dates_are_rejected()
    {
        var f = Valid();
        f.Date = Today.AddDays(1);
        Assert.Contains(nameof(CashForm.Date), Fields(CashEntry.Validate(f, Today)));
    }

    [Fact]
    public void Currency_is_optional_but_must_be_a_code()
    {
        var f = Valid();
        Assert.Null(CashEntry.Validate(f, Today).Value!.Currency);

        f.Currency = "usd";
        Assert.Equal("USD", CashEntry.Validate(f, Today).Value!.Currency);

        f.Currency = "US Dollars";
        Assert.Contains(nameof(CashForm.Currency), Fields(CashEntry.Validate(f, Today)));
    }

    [Fact]
    public void Parameters_match_uspAddCashTransaction_and_leave_blanks_to_the_proc()
    {
        var f = Valid();
        f.Comment = "  ";
        var p = CashEntry.Parameters(9, CashEntry.Validate(f, Today).Value!);

        Assert.Equal(
            ["iUserId", "strBankAccountName", "dtmDateOfTransaction", "dblWithdrawalAmount", "dblDepositAmount",
             "strComment", "iShareTradeId", "strCurrency"],
            p.Keys);
        Assert.Equal(9, p["iUserId"]);
        Assert.Equal(new DateTime(2026, 10, 1), p["dtmDateOfTransaction"]);
        Assert.Equal(0m, p["dblWithdrawalAmount"]);
        Assert.Equal(5000m, p["dblDepositAmount"]);
        Assert.Null(p["strBankAccountName"]);
        Assert.Null(p["strComment"]);
        Assert.Null(p["iShareTradeId"]);
        Assert.Null(p["strCurrency"]);
    }

    [Fact]
    public void Blank_currency_follows_a_known_account()
    {
        BankAccount[] known = [new("IBKR", "USD"), new("Macquarie CMA", "AUD"), new("Mixed", "AUD"), new("Mixed", "USD")];

        Assert.Equal("USD", CashEntry.ResolveCurrency(null, "ibkr", known));
        Assert.Equal("NZD", CashEntry.ResolveCurrency("NZD", "IBKR", known));
        Assert.Null(CashEntry.ResolveCurrency(null, "Mixed", known));
        Assert.Null(CashEntry.ResolveCurrency(null, "New account", known));
        Assert.Null(CashEntry.ResolveCurrency(null, null, known));
    }
}
