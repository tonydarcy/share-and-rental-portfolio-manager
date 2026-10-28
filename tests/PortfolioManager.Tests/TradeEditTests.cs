using PortfolioManager.Core.Trades;

namespace PortfolioManager.Tests;

public class TradeEditTests
{
    private static readonly ShareTrade Stored = new(7, new DateTime(2026, 8, 4), "Buy", "ASX", "BHP", "BHP Group", 100, 44, 9.95, "AUD", 1, 4409.95m, 44.0995, "Sample");
    private static readonly TradeCash Cash = new("Cash Account", new DateTime(2026, 8, 6), "Buy Settlement: 100 BHP");

    [Fact]
    public void Keeps_everything_not_sent()
    {
        var form = TradeEdit.Merge(Stored, null, new TradeChanges { Quantity = 120 });

        Assert.Equal(new DateOnly(2026, 8, 4), form.Date);
        Assert.Equal("Buy", form.Type);
        Assert.Equal("ASX", form.Market);
        Assert.Equal("BHP", form.Code);
        Assert.Equal(120m, form.Quantity);
        Assert.Equal(44m, form.Price);
        Assert.Equal(9.95m, form.Brokerage);
        Assert.Equal("AUD", form.Currency);
        Assert.Equal(1m, form.ExchangeRate);
        Assert.Equal("Sample", form.Comment);
        Assert.False(form.RecordCash);
    }

    [Fact]
    public void Keeps_the_cash_settlement_and_moves_it_with_the_trade_date()
    {
        var form = TradeEdit.Merge(Stored, Cash, new TradeChanges { Date = new DateOnly(2026, 8, 5) });

        Assert.True(form.RecordCash);
        Assert.Equal("Cash Account", form.BankAccount);
        Assert.Equal(new DateOnly(2026, 8, 7), form.SettlementDate);
        // The proc's own comment names the old units, so it writes a new one
        Assert.Null(form.CashComment);
    }

    [Fact]
    public void Keeps_a_comment_someone_typed_and_can_drop_the_cash()
    {
        var typed = Cash with { Comment = "Paid from offset" };
        Assert.Equal("Paid from offset", TradeEdit.Merge(Stored, typed, new TradeChanges()).CashComment);

        var dropped = TradeEdit.Merge(Stored, typed, new TradeChanges { RecordCash = false });
        Assert.False(dropped.RecordCash);
        Assert.Null(dropped.BankAccount);
    }

    [Fact]
    public void Takes_negative_imported_sale_units_as_positive()
    {
        var sale = Stored with { Type = "Sell", Quantity = -50 };
        Assert.Equal(50m, TradeEdit.Merge(sale, null, new TradeChanges()).Quantity);
    }

    [Fact]
    public void Merged_form_passes_the_add_trade_checks()
    {
        var form = TradeEdit.Merge(Stored, Cash, new TradeChanges { Price = 45.5m });
        var result = TradeEntry.Validate(form, new DateOnly(2026, 10, 28));

        Assert.Empty(result.Errors);
        Assert.Equal(45.5m, result.Value!.Price);
        Assert.NotNull(result.Value.Cash);
    }

    [Theory]
    [InlineData("Buy Settlement: 100 BHP", true)]
    [InlineData("sell settlement: 0.5 BTC", true)]
    [InlineData("Settlement from offset", false)]
    [InlineData(null, false)]
    public void Knows_the_procs_own_settlement_comment(string? comment, bool expected)
    {
        Assert.Equal(expected, TradeEdit.IsDefaultSettlementComment(comment));
    }

    [Fact]
    public void Refuses_trades_other_records_point_at()
    {
        Assert.Null(TradeEdit.BlockReason(new TradeEditCheck(0, 0, 0)));
        Assert.Contains("split", TradeEdit.BlockReason(new TradeEditCheck(1, 0, 0)));
        Assert.Contains("demerger", TradeEdit.BlockReason(new TradeEditCheck(0, 1, 0)));
        Assert.Contains("parcel choice", TradeEdit.BlockReason(new TradeEditCheck(0, 0, 2)));
    }
}
