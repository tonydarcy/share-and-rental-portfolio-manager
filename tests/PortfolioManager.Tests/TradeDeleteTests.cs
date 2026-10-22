using PortfolioManager.Core.Trades;

namespace PortfolioManager.Tests;

public class TradeDeleteTests
{
    [Fact]
    public void Allows_a_plain_trade_with_its_cash_transaction_and_groups()
    {
        Assert.Null(TradesService.BlockReason(new TradeDeleteCheck(CgtParcels: 0, ReinvestedPayouts: 0, Journals: 0, CashTransactions: 1, Groups: 2)));
    }

    [Fact]
    public void Allows_a_double_entry_trade_since_the_proc_deletes_its_journals()
    {
        Assert.Null(TradesService.BlockReason(new TradeDeleteCheck(0, 0, Journals: 2, CashTransactions: 1, Groups: 0)));
    }

    [Fact]
    public void Reads_the_procs_success_message()
    {
        Assert.Equal(TradeDeleteOutcome.Deleted, TradesService.ResultFromProc("Share Trade Deleted Successfully").Outcome);
    }

    [Fact]
    public void Reads_the_procs_error_message()
    {
        var result = TradesService.ResultFromProc("Error: This trade is matched to CGT parcels.");
        Assert.Equal(TradeDeleteOutcome.Blocked, result.Outcome);
        Assert.Equal("This trade is matched to CGT parcels.", result.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Something else")]
    public void Treats_an_unexpected_result_as_not_deleted(string? message)
    {
        Assert.Equal(TradeDeleteOutcome.Blocked, TradesService.ResultFromProc(message).Outcome);
    }

    [Fact]
    public void Refuses_a_drp_trade()
    {
        Assert.Contains("DRP", TradesService.BlockReason(new TradeDeleteCheck(0, ReinvestedPayouts: 1, 0, 0, 0)));
    }

    [Fact]
    public void Refuses_a_trade_matched_to_cgt_parcels()
    {
        Assert.Contains("CGT", TradesService.BlockReason(new TradeDeleteCheck(CgtParcels: 3, 0, 0, 0, 0)));
    }
}
