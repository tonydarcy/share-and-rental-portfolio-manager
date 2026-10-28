namespace PortfolioManager.Core.Trades;

/// <summary>The changes to a trade: anything left null keeps the stored value.</summary>
public sealed class TradeChanges
{
    public DateOnly? Date { get; set; }
    public string? Type { get; set; }
    public string? Market { get; set; }
    public string? Code { get; set; }
    public string? Name { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? Price { get; set; }
    public decimal? Brokerage { get; set; }
    public string? Currency { get; set; }
    public decimal? ExchangeRate { get; set; }
    public string? Comment { get; set; }

    /// <summary>Null keeps the cash settlement if the trade has one; false drops it; true adds or keeps it.</summary>
    public bool? RecordCash { get; set; }
    public string? BankAccount { get; set; }
    public DateOnly? SettlementDate { get; set; }
    public string? CashComment { get; set; }
}

/// <summary>The cash transaction recorded with a trade (its settlement).</summary>
public sealed record TradeCash(string? BankAccount, DateTime Date, string? Comment);

/// <summary>What else points at a trade that an edit (delete and re-add under a new id) would leave pointing at nothing.</summary>
public sealed record TradeEditCheck(int SplitAdjustments, int ParcelTransfers, int ParcelChoices);

public enum TradeEditOutcome { Saved, NotFound, Blocked }

public sealed record TradeEditResult(TradeEditOutcome Outcome, string? Reason = null, int? NewTradeId = null);

public static class TradeEdit
{
    /// <summary>
    /// The stored trade with the changes laid over it, as an Add trade form, so it goes through the same checks.
    /// A trade with a cash settlement keeps it (same account and comment) unless told otherwise; when the trade date
    /// moves, the settlement date moves by the same number of days.
    /// </summary>
    public static TradeForm Merge(ShareTrade stored, TradeCash? cash, TradeChanges c)
    {
        var storedDate = DateOnly.FromDateTime(stored.Date);
        var date = c.Date ?? storedDate;
        var form = new TradeForm
        {
            Date = date,
            Type = c.Type ?? stored.Type,
            Market = c.Market ?? stored.ExchangeCode,
            Code = c.Code ?? stored.StockCode,
            Name = c.Name ?? stored.Name,
            // Some imports stored sales as negative units; the form always takes them as positive
            Quantity = c.Quantity ?? Math.Abs((decimal)stored.Quantity),
            Price = c.Price ?? (decimal)stored.PricePerUnit,
            Brokerage = c.Brokerage ?? (decimal)stored.Brokerage,
            Currency = c.Currency ?? stored.Currency,
            ExchangeRate = c.ExchangeRate ?? (decimal)stored.ExchangeRate,
            Comment = c.Comment ?? stored.Comment,
            RecordCash = c.RecordCash ?? cash is not null,
        };
        if (form.RecordCash)
        {
            form.BankAccount = c.BankAccount ?? cash?.BankAccount;
            form.SettlementDate = c.SettlementDate
                ?? (cash is null ? null : DateOnly.FromDateTime(cash.Date).AddDays(date.DayNumber - storedDate.DayNumber));
            // The proc's own "Buy Settlement: 100 BHP" would be out of date, so let it write a new one
            form.CashComment = c.CashComment ?? (cash is null || IsDefaultSettlementComment(cash.Comment) ? null : cash.Comment);
        }
        return form;
    }

    /// <summary>The comment AddShareTradeWithCashTransaction writes when it is given none.</summary>
    public static bool IsDefaultSettlementComment(string? comment) =>
        comment is not null
        && (comment.StartsWith("Buy Settlement:", StringComparison.OrdinalIgnoreCase)
            || comment.StartsWith("Sell Settlement:", StringComparison.OrdinalIgnoreCase));

    /// <summary>Why a trade can't be edited safely (beyond what stops a delete), or null when it can.</summary>
    public static string? BlockReason(TradeEditCheck check)
    {
        if (check.SplitAdjustments > 0)
            return "A share split or consolidation changed this trade. Undo the split, edit the trade, then apply the split again.";
        if (check.ParcelTransfers > 0)
            return "This trade was made by a demerger, takeover or code change. Delete that corporate action and record it again instead.";
        if (check.ParcelChoices > 0)
            return "A sale has a saved parcel choice that uses this trade. Set that sale back to first in, first out on its Parcels page, edit the trade, then choose the parcels again.";
        return null;
    }
}
