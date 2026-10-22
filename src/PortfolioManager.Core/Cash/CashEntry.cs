using PortfolioManager.Core.Validation;

namespace PortfolioManager.Core.Cash;

public enum CashDirection { Deposit, Withdrawal }

/// <summary>A bank account the portfolio has used before (distinct name and currency in tblBankStatement).</summary>
public sealed record BankAccount(string Name, string Currency);

/// <summary>What the Add cash transaction form posts.</summary>
public sealed class CashForm
{
    public DateOnly? Date { get; set; }
    public string? Direction { get; set; }
    public decimal? Amount { get; set; }
    public string? BankAccount { get; set; }
    public string? Currency { get; set; }
    public string? Comment { get; set; }
}

/// <summary>A manual bank entry. Null account or currency lets uspAddCashTransaction pick its defaults.</summary>
public sealed record NewCashTransaction(
    DateOnly Date,
    CashDirection Direction,
    decimal Amount,
    string? BankAccount,
    string? Currency,
    string? Comment)
{
    public decimal Deposit => Direction == CashDirection.Deposit ? Amount : 0m;
    public decimal Withdrawal => Direction == CashDirection.Withdrawal ? Amount : 0m;
}

public static class CashEntry
{
    public static CashDirection? ParseDirection(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "DEPOSIT" => CashDirection.Deposit,
        "WITHDRAWAL" => CashDirection.Withdrawal,
        _ => null,
    };

    public static Validated<NewCashTransaction> Validate(CashForm f, DateOnly today)
    {
        var errors = new List<FieldError>();

        if (f.Date is not { } date)
            errors.Add(new(nameof(f.Date), "Enter the transaction date."));
        else if (date > today)
            errors.Add(new(nameof(f.Date), "The transaction date can't be in the future."));
        else if (date < FormRules.EarliestDate)
            errors.Add(new(nameof(f.Date), "Check the transaction date."));

        var direction = ParseDirection(f.Direction);
        if (direction is null)
            errors.Add(new(nameof(f.Direction), "Choose Deposit or Withdrawal."));

        if (f.Amount is not { } amount)
            errors.Add(new(nameof(f.Amount), "Enter the amount."));
        else if (amount <= 0)
            errors.Add(new(nameof(f.Amount), "The amount must be more than zero."));
        else if (amount > FormRules.MaxMoney)
            errors.Add(new(nameof(f.Amount), "That amount is too large to record."));
        else if (FormRules.HasMoreThanCents(amount))
            errors.Add(new(nameof(f.Amount), "Enter the amount in dollars and cents."));

        var account = FormRules.Clean(f.BankAccount);
        FormRules.MaxLength(errors, nameof(f.BankAccount), "Bank account", account, 500);

        var currency = FormRules.Clean(f.Currency)?.ToUpperInvariant();
        if (currency is not null && !FormRules.IsCurrencyCode(currency))
            errors.Add(new(nameof(f.Currency), "Currency must be a three-letter code such as AUD or USD."));

        var comment = FormRules.Clean(f.Comment);
        FormRules.MaxLength(errors, nameof(f.Comment), "Description", comment, 500);

        if (errors.Count > 0)
            return new(null, errors);

        return new(new NewCashTransaction(f.Date!.Value, direction!.Value, f.Amount!.Value, account, currency, comment), errors);
    }

    /// <summary>
    /// The currency to record in. A blank currency on a known account uses that account's currency
    /// (when it only has one), so the entry joins the right running balance; otherwise the
    /// proc falls back to the portfolio currency.
    /// </summary>
    public static string? ResolveCurrency(string? requested, string? account, IReadOnlyList<BankAccount> known)
    {
        if (requested is not null || account is null)
            return requested;
        var currencies = known
            .Where(a => string.Equals(a.Name, account, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Currency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return currencies.Count == 1 ? currencies[0] : null;
    }

    /// <summary>Parameters for uspAddCashTransaction. iShareTradeId is NULL: a manual entry, not a trade settlement.</summary>
    public static Dictionary<string, object?> Parameters(int portfolioId, NewCashTransaction c) => new()
    {
        ["iUserId"] = portfolioId,
        ["strBankAccountName"] = c.BankAccount,
        ["dtmDateOfTransaction"] = c.Date.ToDateTime(TimeOnly.MinValue),
        ["dblWithdrawalAmount"] = c.Withdrawal,
        ["dblDepositAmount"] = c.Deposit,
        ["strComment"] = c.Comment,
        ["iShareTradeId"] = null,
        ["strCurrency"] = c.Currency,
    };
}
