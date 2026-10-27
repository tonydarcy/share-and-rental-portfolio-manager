using PortfolioManager.Core.Validation;

namespace PortfolioManager.Core.Crypto;

/// <summary>What the Record a crypto swap form posts.</summary>
public sealed class SwapForm
{
    public DateOnly? Date { get; set; }
    /// <summary>The coin given up, e.g. BTC (or BTC-AUD).</summary>
    public string? FromCoin { get; set; }
    public decimal? FromUnits { get; set; }
    /// <summary>The coin received, e.g. ETH.</summary>
    public string? ToCoin { get; set; }
    /// <summary>Units actually received, after any fee the exchange took in coins.</summary>
    public decimal? ToUnits { get; set; }
    /// <summary>The market value of the swap in the portfolio currency on the day.</summary>
    public decimal? Value { get; set; }
    public string? Comment { get; set; }
}

/// <summary>
/// One coin swapped for another. For tax it is a sale of the first coin and a purchase of the second, both at the
/// swap's market value, so it is recorded as a Sell and a Buy at that value with no cash moving.
/// </summary>
public sealed record NewSwap(DateOnly Date, string FromCode, decimal FromUnits, string ToCode, decimal ToUnits, decimal Value,
    string Currency, string Comment)
{
    public decimal SellPrice => Value / FromUnits;
    public decimal BuyPrice => Value / ToUnits;
}

/// <summary>Kinds of income paid in coins.</summary>
public static class RewardKinds
{
    public const string Staking = "Staking";
    public const string Airdrop = "Airdrop";
    public const string Mining = "Mining";
    public const string Other = "Other";

    public static readonly IReadOnlyList<(string Kind, string Label)> All =
    [
        (Staking, "Staking reward"),
        (Airdrop, "Airdrop"),
        (Mining, "Mining reward"),
        (Other, "Other reward (interest, learn-and-earn, referral)"),
    ];

    public static string Label(string kind) => All.FirstOrDefault(k => k.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)).Label ?? "Reward";
}

/// <summary>What the Add a crypto reward form posts.</summary>
public sealed class RewardForm
{
    public DateOnly? Date { get; set; }
    public string? Coin { get; set; }
    public decimal? Units { get; set; }
    /// <summary>The market value of the units received, in the portfolio currency, on the day they were received.</summary>
    public decimal? Value { get; set; }
    public string Kind { get; set; } = RewardKinds.Staking;
    public string? Comment { get; set; }
}

/// <summary>
/// Coins received as income (staking, an airdrop, mining). Their market value when received is income, and it is also the
/// cost base of the new units, so it is recorded like a DRP: other income paid as units bought at that value.
/// </summary>
public sealed record NewReward(DateOnly Date, string Code, decimal Units, decimal Value, string Kind, string Comment)
{
    public decimal Price => Value / Units;
}

public static class CryptoEntry
{
    public static Validated<NewSwap> Validate(SwapForm f, string currency, DateOnly today)
    {
        var errors = new List<FieldError>();
        CheckDate(errors, nameof(f.Date), f.Date, today, "swap");

        var from = Code(errors, nameof(f.FromCoin), f.FromCoin, currency, "Enter the coin you gave up, e.g. BTC.");
        var to = Code(errors, nameof(f.ToCoin), f.ToCoin, currency, "Enter the coin you received, e.g. ETH.");
        if (from is not null && to is not null && from == to)
            errors.Add(new(nameof(f.ToCoin), "The two coins are the same. A swap is from one coin to another."));

        var fromUnits = Units(errors, nameof(f.FromUnits), f.FromUnits, "Enter the units you gave up.");
        var toUnits = Units(errors, nameof(f.ToUnits), f.ToUnits, "Enter the units you received.");
        var value = Value(errors, nameof(f.Value), f.Value, "Enter the swap's market value on the day.");

        var comment = FormRules.Clean(f.Comment) ?? "";
        FormRules.MaxLength(errors, nameof(f.Comment), "Comment", comment, 400);

        if (errors.Count > 0)
            return new(null, errors);
        var note = $"Swap {fromUnits:0.########} {CryptoCodes.Coin(from!)} for {toUnits:0.########} {CryptoCodes.Coin(to!)}" + (comment.Length > 0 ? $": {comment}" : "");
        return new(new NewSwap(f.Date!.Value, from!, fromUnits, to!, toUnits, value, currency.Trim().ToUpperInvariant(), note), errors);
    }

    public static Validated<NewReward> Validate(RewardForm f, string currency, DateOnly today)
    {
        var errors = new List<FieldError>();
        CheckDate(errors, nameof(f.Date), f.Date, today, "reward");
        var code = Code(errors, nameof(f.Coin), f.Coin, currency, "Enter the coin you received, e.g. ETH.");
        var units = Units(errors, nameof(f.Units), f.Units, "Enter the units you received.");
        var value = Value(errors, nameof(f.Value), f.Value, "Enter the market value of the units on the day you received them.");
        var kind = RewardKinds.All.Select(k => k.Kind).FirstOrDefault(k => k.Equals(f.Kind?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (kind is null)
            errors.Add(new(nameof(f.Kind), "Choose the kind of reward."));
        var comment = FormRules.Clean(f.Comment) ?? "";
        FormRules.MaxLength(errors, nameof(f.Comment), "Comment", comment, 400);

        if (errors.Count > 0)
            return new(null, errors);
        return new(new NewReward(f.Date!.Value, code!, units, value, kind!, RewardComment(kind!, comment)), errors);
    }

    public static string RewardComment(string kind, string? note)
    {
        var s = $"Crypto {RewardKinds.Label(kind).ToLowerInvariant()}" + (string.IsNullOrWhiteSpace(note) ? "" : $": {note.Trim()}");
        return s.Length > 500 ? s[..500] : s;
    }

    private static void CheckDate(List<FieldError> errors, string field, DateOnly? date, DateOnly today, string what)
    {
        if (date is not { } d)
            errors.Add(new(field, $"Enter the date of the {what}."));
        else if (d > today)
            errors.Add(new(field, "The date can't be in the future."));
        else if (d < FormRules.EarliestDate)
            errors.Add(new(field, "Check the date."));
    }

    private static string? Code(List<FieldError> errors, string field, string? coin, string currency, string missing)
    {
        if (FormRules.Clean(coin) is not { } c)
        {
            errors.Add(new(field, missing));
            return null;
        }
        var code = CryptoCodes.SiteCode(c, currency);
        if (code.Length > 50)
        {
            errors.Add(new(field, "The coin code must be 50 characters or fewer."));
            return null;
        }
        return code;
    }

    private static decimal Units(List<FieldError> errors, string field, decimal? units, string missing)
    {
        if (units is not { } u)
        {
            errors.Add(new(field, missing));
            return 0;
        }
        if (FormRules.UnitsProblem(u) is { } problem)
        {
            errors.Add(new(field, problem));
            return 0;
        }
        return u;
    }

    private static decimal Value(List<FieldError> errors, string field, decimal? value, string missing)
    {
        if (value is not { } v)
            errors.Add(new(field, missing));
        else if (v < 0.01m)
            errors.Add(new(field, "The value must be at least one cent."));
        else if (v > FormRules.MaxMoney)
            errors.Add(new(field, "That value is too large to record."));
        else if (FormRules.HasMoreThanCents(v))
            errors.Add(new(field, "Enter the value to the cent."));
        else
            return v;
        return 0;
    }
}
