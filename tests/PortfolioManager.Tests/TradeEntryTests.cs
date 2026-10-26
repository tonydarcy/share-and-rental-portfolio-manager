using PortfolioManager.Core.Cash;
using PortfolioManager.Core.Trades;
using PortfolioManager.Core.Validation;

namespace PortfolioManager.Tests;

public class TradeEntryTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static TradeForm Valid() => new()
    {
        Date = new DateOnly(2026, 10, 14),
        Type = "Buy",
        Market = " asx ",
        Code = "cba",
        Quantity = 100,
        Price = 150.25m,
        Brokerage = 9.95m,
        Currency = "aud",
        ExchangeRate = 1m,
    };

    private static IEnumerable<string> Fields(Validated<NewTrade> r) => r.Errors.Select(e => e.Field);

    [Fact]
    public void A_valid_buy_is_cleaned_up()
    {
        var r = TradeEntry.Validate(Valid(), Today);

        Assert.True(r.IsValid);
        var t = r.Value!;
        Assert.Equal(TradeType.Buy, t.Type);
        Assert.Equal("Buy", t.TypeText);
        Assert.Equal("ASX", t.Market);
        Assert.Equal("CBA", t.Code);
        Assert.Equal("AUD", t.Currency);
        Assert.Null(t.Name);
        Assert.Equal("", t.Comment);
        Assert.Null(t.Cash);
        Assert.Equal(15_034.95m, t.OrderTotal);
    }

    [Theory]
    [InlineData("SELL", TradeType.Sell)]
    [InlineData("sell", TradeType.Sell)]
    [InlineData("BUY", TradeType.Buy)]
    [InlineData(" Buy ", TradeType.Buy)]
    public void Type_accepts_buy_or_sell_in_any_case(string input, TradeType expected) =>
        Assert.Equal(expected, TradeEntry.ParseType(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Hold")]
    public void Type_must_be_buy_or_sell(string? input)
    {
        var f = Valid();
        f.Type = input;
        Assert.Contains(nameof(TradeForm.Type), Fields(TradeEntry.Validate(f, Today)));
    }

    [Fact]
    public void Required_fields_are_reported()
    {
        var r = TradeEntry.Validate(new TradeForm(), Today);

        Assert.False(r.IsValid);
        Assert.Null(r.Value);
        Assert.Equivalent(
            new[] { "Date", "Type", "Market", "Code", "Quantity", "Price", "Currency", "ExchangeRate" },
            Fields(r).Distinct().ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10.123456789)]
    public void Units_must_be_positive_with_at_most_eight_decimals(double units)
    {
        var f = Valid();
        f.Quantity = (decimal)units;
        Assert.Contains(nameof(TradeForm.Quantity), Fields(TradeEntry.Validate(f, Today)));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.00012345)]
    public void Part_units_are_accepted(double units)
    {
        var f = Valid();
        f.Quantity = (decimal)units;
        var t = TradeEntry.Validate(f, Today).Value!;
        Assert.Equal((decimal)units, t.Quantity);
        Assert.Equal(units, (double)TradeEntry.ShareTradeParameters(1, t)["iQuantity"]!, 10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Price_and_rate_must_be_positive(double value)
    {
        var f = Valid();
        f.Price = (decimal)value;
        f.ExchangeRate = (decimal)value;
        var fields = Fields(TradeEntry.Validate(f, Today)).ToList();
        Assert.Contains(nameof(TradeForm.Price), fields);
        Assert.Contains(nameof(TradeForm.ExchangeRate), fields);
    }

    [Fact]
    public void Brokerage_can_be_blank_but_not_negative()
    {
        var f = Valid();
        f.Brokerage = null;
        Assert.Equal(0m, TradeEntry.Validate(f, Today).Value!.Brokerage);

        f.Brokerage = -1m;
        Assert.Contains(nameof(TradeForm.Brokerage), Fields(TradeEntry.Validate(f, Today)));
    }

    [Fact]
    public void Sale_brokerage_cannot_exceed_proceeds()
    {
        var f = Valid();
        f.Type = "Sell";
        f.Quantity = 1;
        f.Price = 5m;
        f.Brokerage = 9.95m;
        Assert.Contains(nameof(TradeForm.Brokerage), Fields(TradeEntry.Validate(f, Today)));
    }

    [Fact]
    public void Trades_too_large_for_the_order_total_column_are_rejected()
    {
        var f = Valid();
        f.Quantity = 1_000_000;
        f.Price = 100m;
        Assert.Contains(nameof(TradeForm.Quantity), Fields(TradeEntry.Validate(f, Today)));
    }

    [Theory]
    [InlineData(2026, 10, 16)]
    [InlineData(1899, 12, 31)]
    public void Trade_date_must_be_sensible(int y, int m, int d)
    {
        var f = Valid();
        f.Date = new DateOnly(y, m, d);
        Assert.Contains(nameof(TradeForm.Date), Fields(TradeEntry.Validate(f, Today)));
    }

    [Fact]
    public void Today_is_allowed()
    {
        var f = Valid();
        f.Date = Today;
        Assert.True(TradeEntry.Validate(f, Today).IsValid);
    }

    [Theory]
    [InlineData("AU")]
    [InlineData("AUDD")]
    [InlineData("A$D")]
    public void Currency_must_be_a_three_letter_code(string currency)
    {
        var f = Valid();
        f.Currency = currency;
        Assert.Contains(nameof(TradeForm.Currency), Fields(TradeEntry.Validate(f, Today)));
    }

    [Fact]
    public void Long_text_is_rejected()
    {
        var f = Valid();
        f.Code = new string('X', 51);
        f.Comment = new string('x', 501);
        var fields = Fields(TradeEntry.Validate(f, Today)).ToList();
        Assert.Contains(nameof(TradeForm.Code), fields);
        Assert.Contains(nameof(TradeForm.Comment), fields);
    }

    [Fact]
    public void Settlement_must_not_be_before_the_trade()
    {
        var f = Valid();
        f.RecordCash = true;
        f.SettlementDate = f.Date!.Value.AddDays(-1);
        Assert.Contains(nameof(TradeForm.SettlementDate), Fields(TradeEntry.Validate(f, Today)));

        f.SettlementDate = f.Date!.Value.AddDays(91);
        Assert.Contains(nameof(TradeForm.SettlementDate), Fields(TradeEntry.Validate(f, Today)));
    }

    [Fact]
    public void Settlement_date_may_be_after_today()
    {
        var f = Valid();
        f.Date = Today;
        f.RecordCash = true;
        f.SettlementDate = Today.AddDays(2);
        Assert.True(TradeEntry.Validate(f, Today).IsValid);
    }

    [Fact]
    public void Cash_fields_are_ignored_unless_recording_cash()
    {
        var f = Valid();
        f.RecordCash = false;
        f.SettlementDate = f.Date!.Value.AddDays(-10);
        f.BankAccount = "Macquarie CMA";
        var r = TradeEntry.Validate(f, Today);
        Assert.True(r.IsValid);
        Assert.Null(r.Value!.Cash);
    }

    [Fact]
    public void Foreign_trade_total_is_converted_to_portfolio_currency()
    {
        var f = Valid();
        f.Market = "NASDAQ";
        f.Code = "AAPL";
        f.Currency = "USD";
        f.Quantity = 10;
        f.Price = 200m;
        f.Brokerage = 5m;
        f.ExchangeRate = 0.65m;
        var t = TradeEntry.Validate(f, Today).Value!;
        Assert.Equal(2005m, t.OrderTotal);
        Assert.Equal(3084.62m, t.PortfolioCurrencyTotal);
    }

    [Fact]
    public void Trade_only_parameters_match_uspAddShareTrade()
    {
        var t = TradeEntry.Validate(Valid(), Today).Value!;
        var p = TradeEntry.ShareTradeParameters(42, t);

        Assert.Equal(
            ["iUserId", "strMarket", "strCode", "strName", "strType", "dtmDateOfTransaction", "iQuantity",
             "dblPricePerUnit", "dblBrokerage", "strCurrency", "dblExchangeRate", "strComment", "iBankStatementId"],
            p.Keys);
        Assert.Equal(42, p["iUserId"]);
        Assert.Equal("Buy", p["strType"]);
        Assert.Equal(new DateTime(2026, 10, 14), p["dtmDateOfTransaction"]);
        Assert.Equal(100d, p["iQuantity"]);
        Assert.Equal("", p["strComment"]);
        Assert.Null(p["iBankStatementId"]);
    }

    [Fact]
    public void With_cash_parameters_match_AddShareTradeWithCashTransaction_and_leave_blanks_to_the_proc()
    {
        var f = Valid();
        f.RecordCash = true;
        f.Type = "sell";
        var t = TradeEntry.Validate(f, Today).Value!;
        var p = TradeEntry.ShareTradeWithCashParameters(7, t);

        Assert.Equal(
            ["iUserId", "strMarket", "strCode", "strName", "strType", "dtmDateOfTransaction", "iQuantity",
             "dblPricePerUnit", "dblBrokerage", "strCurrency", "dblExchangeRate", "strShareTradeComment",
             "strBankAccountName", "dtmSettlementDate", "strCashTransactionComment"],
            p.Keys);
        Assert.Equal("Sell", p["strType"]);
        Assert.Null(p["strBankAccountName"]);
        Assert.Null(p["dtmSettlementDate"]);
        Assert.Null(p["strCashTransactionComment"]);
    }

    [Fact]
    public void With_cash_parameters_carry_the_chosen_account_and_date()
    {
        var f = Valid();
        f.RecordCash = true;
        f.BankAccount = " Macquarie CMA ";
        f.SettlementDate = new DateOnly(2026, 10, 16);
        var p = TradeEntry.ShareTradeWithCashParameters(7, TradeEntry.Validate(f, Today).Value!);

        Assert.Equal("Macquarie CMA", p["strBankAccountName"]);
        Assert.Equal(new DateTime(2026, 10, 16), p["dtmSettlementDate"]);
    }

    [Fact]
    public void Settling_into_an_account_in_another_currency_is_flagged()
    {
        var f = Valid();
        f.RecordCash = true;
        f.BankAccount = "IBKR USD";
        var t = TradeEntry.Validate(f, Today).Value!;
        BankAccount[] accounts = [new("IBKR USD", "USD"), new("Macquarie CMA", "AUD")];

        Assert.Equal(nameof(TradeForm.BankAccount), TradeEntry.CheckSettlementAccount(t, "AUD", accounts)?.Field);

        Assert.Null(TradeEntry.CheckSettlementAccount(t with { Cash = t.Cash! with { BankAccount = "macquarie cma" } }, "AUD", accounts));
        Assert.Null(TradeEntry.CheckSettlementAccount(t with { Cash = t.Cash! with { BankAccount = "New account" } }, "AUD", accounts));
        Assert.Null(TradeEntry.CheckSettlementAccount(t with { Cash = t.Cash! with { BankAccount = null } }, "AUD", accounts));
    }
}
