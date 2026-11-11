namespace PortfolioManager.Mobile.Client;

/// <summary>A share code found by search_securities.</summary>
public sealed record SecurityMatch(string Exchange, string Code, string? Name, string? Sector);

/// <summary>A buy or sale for add_trade. The app records trades in the portfolio's own currency only.</summary>
public sealed record TradeRequest(
    DateOnly Date, string Type, string Market, string Code, decimal Quantity, decimal Price, decimal Brokerage,
    string Currency, decimal ExchangeRate = 1, string? Comment = null, bool RecordCash = false);

/// <summary>A dividend or distribution for add_payout. Total = franked + unfranked (the cash received).</summary>
public sealed record PayoutRequest(
    DateOnly Date, string StockExchangeCode, string StockCode, decimal Total, decimal Franked, decimal Unfranked,
    decimal FrankingCredit, string? Comment = null);

/// <summary>A bank deposit or withdrawal for add_cash_transaction.</summary>
public sealed record CashRequest(DateOnly Date, string Direction, decimal Amount, string? Comment = null);

/// <summary>A file (e.g. a photo of a contract note) for add_attachment.</summary>
public sealed record AttachmentRequest(string Kind, int TargetId, string FileName, string ContentBase64, string? Note = null);

/// <summary>The site's reply to a write: its message, and the new trade's id for add_trade.</summary>
public sealed record SavedReply(string? Message, int? TradeId);

/// <summary>Checks on the phone before sending, so simple slips get a plain message without a round trip.</summary>
public static class EntryChecks
{
    public const int MaxAttachmentBytes = 10 * 1024 * 1024;

    public static IReadOnlyList<string> Trade(TradeRequest t, DateOnly today)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(t.Code))
            errors.Add("Pick the share.");
        if (t.Quantity <= 0 || t.Quantity != decimal.Truncate(t.Quantity))
            errors.Add("Units must be a whole number above zero.");
        if (t.Price <= 0)
            errors.Add("Price must be above zero.");
        if (t.Brokerage < 0)
            errors.Add("Brokerage can't be negative.");
        if (t.Date > today)
            errors.Add("The trade date can't be in the future.");
        return errors;
    }

    public static IReadOnlyList<string> Payout(PayoutRequest p, DateOnly today)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(p.StockCode))
            errors.Add("Pick the share.");
        if (p.Total <= 0)
            errors.Add("Cash received must be above zero.");
        if (p.Franked < 0 || p.Franked > p.Total)
            errors.Add("The franked part must be between zero and the cash received.");
        if (p.FrankingCredit < 0)
            errors.Add("Franking credit can't be negative.");
        if (p.Date > today.AddYears(1))
            errors.Add("Check the payment date.");
        return errors;
    }

    public static IReadOnlyList<string> Cash(CashRequest c)
    {
        var errors = new List<string>();
        if (c.Amount <= 0)
            errors.Add("Amount must be above zero.");
        if (c.Direction is not ("Deposit" or "Withdrawal"))
            errors.Add("Pick deposit or withdrawal.");
        return errors;
    }

    public static IReadOnlyList<string> Rental(RentalRequest r, DateOnly today)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(r.PropertyCode))
            errors.Add("Pick the property.");
        if (r.Amounts.Values.Any(a => a < 0))
            errors.Add("Enter amounts as positive numbers; expenses are taken off the rent.");
        else if (r.Amounts.Values.All(a => a == 0))
            errors.Add("Enter the rent received or at least one expense.");
        if (r.Date > today.AddYears(1))
            errors.Add("Check the date.");
        if ((r.Comment?.Length ?? 0) > 500)
            errors.Add("The description can be at most 500 characters.");
        return errors;
    }

    /// <summary>The franking credit for a fully franked amount at a company tax rate (30% or 25%).</summary>
    public static decimal FrankingCredit(decimal franked, decimal companyTaxRate) =>
        companyTaxRate is <= 0 or >= 1 ? 0 : Math.Round(franked * companyTaxRate / (1 - companyTaxRate), 2, MidpointRounding.AwayFromZero);
}
