namespace PortfolioManager.Core.Import;

/// <summary>A trade already in tblShareTrades. Sells may have been stored with a negative quantity.</summary>
public sealed record ExistingTrade(string Exchange, string Code, string Type, DateTime Date, decimal Quantity, decimal Price);

/// <summary>A payout already in tblPayouts; Total is the cash received (dblTotal).</summary>
public sealed record ExistingPayout(string Exchange, string Code, DateTime Date, decimal Total);

/// <summary>Compares parsed rows with what the portfolio already holds, so re-running an import doesn't double up.</summary>
public static class NavexaDuplicates
{
    private const decimal PriceTolerance = 0.0001m;
    private const decimal AmountTolerance = 0.01m;

    /// <summary>
    /// Same exchange, code, type, date, units and price is a duplicate and won't be imported.
    /// Same holding, type and date with different numbers is only a warning.
    /// </summary>
    public static void MarkTrades(IEnumerable<NavexaTrade> rows, IEnumerable<ExistingTrade> existing)
    {
        var byDay = existing.ToLookup(e => (Key(e.Exchange), Key(e.Code), Key(e.Type), e.Date.Date));
        foreach (var row in rows.Where(r => r.Status == ImportRowStatus.Ready))
        {
            var sameDay = byDay[(Key(row.Exchange), Key(row.Code), Key(row.Type), row.Date.Date)].ToList();
            var match = sameDay.FirstOrDefault(e =>
                Math.Abs(e.Quantity) == row.Quantity && Math.Abs(e.Price - row.Price) <= PriceTolerance);
            if (match is not null)
                row.DuplicateOf = $"Already in this portfolio: {match.Type} {Math.Abs(match.Quantity):#,##0} {match.Code} on {match.Date:d} at {match.Price:0.####}.";
            else if (sameDay.Count > 0)
            {
                var e = sameDay[0];
                row.Warnings.Add($"This portfolio already has a {e.Type} of {e.Code} on {e.Date:d} ({Math.Abs(e.Quantity):#,##0} at {e.Price:0.####}). Check it isn't the same trade.");
            }
        }
    }

    /// <summary>Same exchange, code, payment date and cash amount is a duplicate; same day with a different amount is a warning.</summary>
    public static void MarkPayouts(IEnumerable<NavexaPayout> rows, IEnumerable<ExistingPayout> existing)
    {
        var byDay = existing.ToLookup(e => (Key(e.Exchange), Key(e.Code), e.Date.Date));
        foreach (var row in rows.Where(r => r.Status == ImportRowStatus.Ready))
        {
            var sameDay = byDay[(Key(row.Exchange), Key(row.Code), row.Date.Date)].ToList();
            var match = sameDay.FirstOrDefault(e => Math.Abs(e.Total - row.Net) <= AmountTolerance);
            if (match is not null)
                row.DuplicateOf = $"Already in this portfolio: {match.Code} payout of {match.Total:C} on {match.Date:d}.";
            else if (sameDay.Count > 0)
                row.Warnings.Add($"This portfolio already has a {row.Code} payout on {row.Date:d} of {sameDay[0].Total:C}. Check it isn't the same payment.");
        }
    }

    private static string Key(string s) => s.Trim().ToUpperInvariant();
}
