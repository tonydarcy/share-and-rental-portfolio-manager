using PortfolioManager.Core.Cash;
using PortfolioManager.Core.Validation;

namespace PortfolioManager.Core.Trades;

public enum TradeType { Buy, Sell }

/// <summary>What the Add trade form posts. Everything is optional here so validation can explain what's missing.</summary>
public sealed class TradeForm
{
    public DateOnly? Date { get; set; }
    public string? Type { get; set; }
    public string? Market { get; set; }
    public string? Code { get; set; }
    public string? Name { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? Price { get; set; }
    public decimal? Brokerage { get; set; }

    /// <summary>India: the securities transaction tax (STT) included in the brokerage, kept out of the capital gains cost.</summary>
    public decimal? SecuritiesTax { get; set; }
    public string? Currency { get; set; }
    public decimal? ExchangeRate { get; set; }
    public string? Comment { get; set; }

    /// <summary>Also record the matching bank withdrawal (buy) or deposit (sell).</summary>
    public bool RecordCash { get; set; }
    public string? BankAccount { get; set; }
    public DateOnly? SettlementDate { get; set; }
    public string? CashComment { get; set; }

    /// <summary>Ticked after the site warns that a sale is for more units than are held on that date.</summary>
    public bool SellMoreThanHeld { get; set; }
}

/// <summary>Bank details for the cash side of a trade. Nulls let the proc pick its defaults.</summary>
public sealed record CashSettlement(string? BankAccount, DateOnly? SettlementDate, string? Comment);

public sealed record NewTrade(
    DateOnly Date,
    TradeType Type,
    string Market,
    string Code,
    string? Name,
    decimal Quantity,
    decimal Price,
    decimal Brokerage,
    string Currency,
    decimal ExchangeRate,
    string Comment,
    CashSettlement? Cash,
    decimal SecuritiesTax = 0m)
{
    /// <summary>Value of the trade in its own currency, as uspAddShareTrade works it out.</summary>
    public decimal OrderTotal => TradeEntry.OrderTotal(Type, Quantity, Price, Brokerage);

    /// <summary>The cash that moves, in portfolio currency, as AddShareTradeWithCashTransaction works it out.</summary>
    public decimal PortfolioCurrencyTotal => Math.Round(OrderTotal / ExchangeRate, 2, MidpointRounding.AwayFromZero);

    /// <summary>The value the proc stores in tblShareTrades.strType (its check constraint allows only Buy or Sell).</summary>
    public string TypeText => Type == TradeType.Buy ? "Buy" : "Sell";
}

public static class TradeEntry
{
    public static decimal OrderTotal(TradeType type, decimal quantity, decimal price, decimal brokerage) =>
        type == TradeType.Buy ? price * quantity + brokerage : price * quantity - brokerage;

    public static TradeType? ParseType(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "BUY" => TradeType.Buy,
        "SELL" => TradeType.Sell,
        _ => null,
    };

    public static Validated<NewTrade> Validate(TradeForm f, DateOnly today)
    {
        var errors = new List<FieldError>();

        if (f.Date is not { } date)
            errors.Add(new(nameof(f.Date), "Enter the trade date."));
        else if (date > today)
            errors.Add(new(nameof(f.Date), "The trade date can't be in the future."));
        else if (date < FormRules.EarliestDate)
            errors.Add(new(nameof(f.Date), "Check the trade date."));

        var type = ParseType(f.Type);
        if (type is null)
            errors.Add(new(nameof(f.Type), "Choose Buy or Sell."));

        var market = FormRules.Clean(f.Market)?.ToUpperInvariant();
        if (market is null)
            errors.Add(new(nameof(f.Market), "Enter the stock exchange, e.g. ASX."));
        FormRules.MaxLength(errors, nameof(f.Market), "Stock exchange", market, 50);

        var code = FormRules.Clean(f.Code)?.ToUpperInvariant();
        if (code is null)
            errors.Add(new(nameof(f.Code), "Enter the stock code."));
        FormRules.MaxLength(errors, nameof(f.Code), "Stock code", code, 50);

        var name = FormRules.Clean(f.Name);
        FormRules.MaxLength(errors, nameof(f.Name), "Stock name", name, 500);

        decimal quantity = 0;
        if (f.Quantity is not { } q)
            errors.Add(new(nameof(f.Quantity), "Enter the number of units."));
        else if (FormRules.UnitsProblem(q) is { } problem)
            errors.Add(new(nameof(f.Quantity), problem));
        else
            quantity = q;

        if (f.Price is not { } price)
            errors.Add(new(nameof(f.Price), "Enter the price per unit."));
        else if (price <= 0)
            errors.Add(new(nameof(f.Price), "Price must be more than zero."));

        var brokerage = f.Brokerage ?? 0m;
        if (brokerage < 0)
            errors.Add(new(nameof(f.Brokerage), "Brokerage can't be negative."));
        var securitiesTax = f.SecuritiesTax ?? 0m;
        if (securitiesTax < 0)
            errors.Add(new(nameof(f.SecuritiesTax), "Securities transaction tax can't be negative."));
        else if (securitiesTax > brokerage)
            errors.Add(new(nameof(f.SecuritiesTax), "Securities transaction tax is part of the brokerage, so it can't be more than the brokerage."));

        var currency = FormRules.Clean(f.Currency)?.ToUpperInvariant();
        if (currency is null)
            errors.Add(new(nameof(f.Currency), "Enter the trade currency, e.g. AUD."));
        else if (!FormRules.IsCurrencyCode(currency))
            errors.Add(new(nameof(f.Currency), "Currency must be a three-letter code such as AUD or USD."));

        if (f.ExchangeRate is not { } rate)
            errors.Add(new(nameof(f.ExchangeRate), "Enter the exchange rate (1 when the trade is in portfolio currency)."));
        else if (rate <= 0)
            errors.Add(new(nameof(f.ExchangeRate), "Exchange rate must be more than zero."));

        var comment = FormRules.Clean(f.Comment) ?? "";
        FormRules.MaxLength(errors, nameof(f.Comment), "Comment", comment, 500);

        // The order total is stored in a decimal(10,2) column, and a sale can't net less than nothing
        if (type is { } t && quantity > 0 && f.Price is > 0 && brokerage >= 0)
        {
            var total = OrderTotal(t, quantity, f.Price.Value, brokerage);
            if (total > FormRules.MaxMoney || (f.ExchangeRate is > 0 && total / f.ExchangeRate.Value > FormRules.MaxMoney))
                errors.Add(new(nameof(f.Quantity), "That trade is too large to record."));
            else if (total <= 0)
                errors.Add(new(nameof(f.Brokerage), "Brokerage can't be more than the sale proceeds."));
        }

        CashSettlement? cash = null;
        if (f.RecordCash)
        {
            var account = FormRules.Clean(f.BankAccount);
            FormRules.MaxLength(errors, nameof(f.BankAccount), "Bank account", account, 500);

            if (f.SettlementDate is { } settle && f.Date is { } tradeDate)
            {
                if (settle < tradeDate)
                    errors.Add(new(nameof(f.SettlementDate), "Settlement can't be before the trade date."));
                else if (settle > tradeDate.AddDays(90))
                    errors.Add(new(nameof(f.SettlementDate), "Settlement should be within 90 days of the trade."));
            }

            var cashComment = FormRules.Clean(f.CashComment);
            FormRules.MaxLength(errors, nameof(f.CashComment), "Cash comment", cashComment, 500);
            cash = new CashSettlement(account, f.SettlementDate, cashComment);
        }

        if (errors.Count > 0)
            return new(null, errors);

        return new(new NewTrade(f.Date!.Value, type!.Value, market!, code!, name, quantity, f.Price!.Value, brokerage,
            currency!, f.ExchangeRate!.Value, comment, cash, securitiesTax), errors);
    }

    /// <summary>
    /// AddShareTradeWithCashTransaction always books the cash in portfolio currency (it divides by the
    /// exchange rate and doesn't pass a currency on), so settling into an account kept in another
    /// currency would mix currencies in that account's running balance.
    /// </summary>
    public static FieldError? CheckSettlementAccount(NewTrade trade, string portfolioCurrency, IReadOnlyList<BankAccount> accounts)
    {
        if (trade.Cash?.BankAccount is not { } name)
            return null;
        var currencies = accounts
            .Where(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Currency)
            .ToList();
        if (currencies.Count == 0 || currencies.Any(c => string.Equals(c, portfolioCurrency, StringComparison.OrdinalIgnoreCase)))
            return null;
        return new(nameof(TradeForm.BankAccount),
            $"Trade settlements are recorded in {portfolioCurrency}, but {name} is a {string.Join("/", currencies)} account. Pick a {portfolioCurrency} account, or untick the cash option and add the cash transaction separately.");
    }

    /// <summary>
    /// A sale of more units than the portfolio holds on that date is almost always a typo or a missing buy,
    /// and leaves a negative holding. It is refused unless the user ticks that they mean it.
    /// </summary>
    public static FieldError? CheckOversell(NewTrade trade, decimal unitsHeld, bool confirmed)
    {
        if (trade.Type != TradeType.Sell || confirmed || trade.Quantity <= unitsHeld)
            return null;
        var held = unitsHeld <= 0 ? "no" : $"only {unitsHeld:#,##0.########}";
        return new(nameof(TradeForm.Quantity),
            $"You hold {held} {trade.Code} units on {trade.Date:d MMM yyyy}. Check the units, add the missing buy first, or tick the box below to record it anyway.");
    }

    /// <summary>Parameters for uspAddShareTrade (trade only, no bank entry).</summary>
    public static Dictionary<string, object?> ShareTradeParameters(int portfolioId, NewTrade t) => new()
    {
        ["iUserId"] = portfolioId,
        ["strMarket"] = t.Market,
        ["strCode"] = t.Code,
        ["strName"] = t.Name,
        ["strType"] = t.TypeText,
        ["dtmDateOfTransaction"] = t.Date.ToDateTime(TimeOnly.MinValue),
        ["iQuantity"] = (double)t.Quantity,
        ["dblPricePerUnit"] = t.Price,
        ["dblBrokerage"] = t.Brokerage,
        ["strCurrency"] = t.Currency,
        ["dblExchangeRate"] = t.ExchangeRate,
        ["strComment"] = t.Comment,
        ["iBankStatementId"] = null,
    };

    /// <summary>
    /// Parameters for AddShareTradeWithCashTransaction. Blank bank account, settlement date and
    /// cash comment go as NULL so the proc uses its defaults (latest account used, T+2, "Buy Settlement: ...").
    /// </summary>
    public static Dictionary<string, object?> ShareTradeWithCashParameters(int portfolioId, NewTrade t)
    {
        var cash = t.Cash ?? throw new ArgumentException("The trade has no cash settlement.", nameof(t));
        return new()
        {
            ["iUserId"] = portfolioId,
            ["strMarket"] = t.Market,
            ["strCode"] = t.Code,
            ["strName"] = t.Name,
            ["strType"] = t.TypeText,
            ["dtmDateOfTransaction"] = t.Date.ToDateTime(TimeOnly.MinValue),
            ["iQuantity"] = (double)t.Quantity,
            ["dblPricePerUnit"] = t.Price,
            ["dblBrokerage"] = t.Brokerage,
            ["strCurrency"] = t.Currency,
            ["dblExchangeRate"] = t.ExchangeRate,
            ["strShareTradeComment"] = t.Comment,
            ["strBankAccountName"] = cash.BankAccount,
            ["dtmSettlementDate"] = cash.SettlementDate?.ToDateTime(TimeOnly.MinValue),
            ["strCashTransactionComment"] = cash.Comment,
        };
    }
}
