using System.Data;
using System.Data.Common;
using Dapper;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Trades;

namespace PortfolioManager.Core.Crypto;

/// <summary>
/// Records crypto swaps and rewards through the trade and DRP procs, which post the journals for double-entry portfolios.
/// A swap's Sell and Buy both post against Trade Clearing at the same value, so the clearing account nets to nil.
/// </summary>
public sealed class CryptoService(IDbConnectionFactory db)
{
    /// <summary>The Sell and the Buy in one database transaction: both are recorded or neither is.</summary>
    public async Task AddSwapAsync(int portfolioId, NewSwap swap)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        await PostSwapAsync(con, tx, portfolioId, swap);
        await tx.CommitAsync();
    }

    public async Task<ProcResult> AddRewardAsync(int portfolioId, NewReward reward)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        var result = await PostRewardAsync(con, tx, portfolioId, reward);
        if (result.Ok)
            await tx.CommitAsync();
        else
            await tx.RollbackAsync();
        return result;
    }

    /// <summary>
    /// The rows of an exchange file in one database transaction, so a failure part-way leaves nothing behind. Buys and
    /// sells settle in the bank account when withCash is set; swaps and rewards never move cash.
    /// </summary>
    public async Task<ProcResult> ImportAsync(int portfolioId, IReadOnlyList<CryptoImportRow> rows, bool withCash)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        foreach (var row in rows.OrderBy(r => r.Date).ThenBy(r => r.PostingOrder).ThenBy(r => r.Line))
        {
            if (row.Trade is { } t)
            {
                var trade = withCash ? t with { Cash = new CashSettlement(null, null, null) } : t;
                var (proc, parameters) = trade.Cash is null
                    ? ("uspAddShareTrade", TradeEntry.ShareTradeParameters(portfolioId, trade))
                    : ("AddShareTradeWithCashTransaction", TradeEntry.ShareTradeWithCashParameters(portfolioId, trade));
                await con.ExecuteAsync(proc, new DynamicParameters(parameters), tx, commandType: CommandType.StoredProcedure);
            }
            else if (row.Swap is { } s)
                await PostSwapAsync(con, tx, portfolioId, s);
            else if (row.Reward is { } r && await PostRewardAsync(con, tx, portfolioId, r) is { Ok: false } failed)
            {
                await tx.RollbackAsync();
                return ProcResult.Failed($"Line {row.Line}: {failed.Error}");
            }
        }
        await tx.CommitAsync();
        return ProcResult.Success();
    }

    public static async Task PostSwapAsync(DbConnection con, DbTransaction tx, int portfolioId, NewSwap swap)
    {
        foreach (var trade in SwapTrades(swap))
            await con.ExecuteAsync("uspAddShareTrade", new DynamicParameters(TradeEntry.ShareTradeParameters(portfolioId, trade)), tx,
                commandType: CommandType.StoredProcedure);
    }

    public static async Task<ProcResult> PostRewardAsync(DbConnection con, DbTransaction tx, int portfolioId, NewReward reward)
    {
        var row = (IDictionary<string, object?>?)await con.QueryFirstOrDefaultAsync("uspAddDRPTransaction",
            new DynamicParameters(RewardParameters(portfolioId, reward)), tx, commandType: CommandType.StoredProcedure);
        var text = row is not null && row.TryGetValue("Result", out var v) && v is not null and not DBNull ? Convert.ToString(v) : null;
        return ProcResult.From(text, "DRP Added");
    }

    /// <summary>The Sell of the coin given up, then the Buy of the coin received, both at the swap's value and without cash.</summary>
    public static IReadOnlyList<NewTrade> SwapTrades(NewSwap s) =>
    [
        new(s.Date, TradeType.Sell, CryptoCodes.Market, s.FromCode, null, s.FromUnits, s.SellPrice, 0, s.Currency, 1, s.Comment, null),
        new(s.Date, TradeType.Buy, CryptoCodes.Market, s.ToCode, null, s.ToUnits, s.BuyPrice, 0, s.Currency, 1, s.Comment, null),
    ];

    /// <summary>
    /// uspAddDRPTransaction with the value as other income (2026-10-27-03-crypto-rewards.sql): the payout and the Buy of
    /// the units at their market value, with no franking.
    /// </summary>
    public static Dictionary<string, object?> RewardParameters(int portfolioId, NewReward r) => new()
    {
        ["iUserId"] = portfolioId,
        ["strStockExchangeCode"] = CryptoCodes.Market,
        ["strStockCode"] = r.Code,
        ["dtmTransactionDate"] = r.Date.ToDateTime(TimeOnly.MinValue),
        ["dblTotalPayout"] = (double)r.Value,
        ["dblFrankedAmount"] = 0d,
        ["dblUnFrankedAmount"] = 0d,
        ["dblFrankingCredit"] = 0d,
        ["iQuantity"] = (double)r.Units,
        ["dblPricePerUnit"] = (double)r.Price,
        ["strComment"] = r.Comment,
        ["dblTFNWithholdingAmount"] = 0d,
        ["dblOtherAmount"] = (double)r.Value,
    };
}
