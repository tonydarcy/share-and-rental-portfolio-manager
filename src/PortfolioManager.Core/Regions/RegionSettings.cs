using System.Globalization;

namespace PortfolioManager.Core.Regions;

/// <summary>The day a financial year starts, e.g. 1 July in Australia or 6 April in the UK.</summary>
public sealed record YearStart(int Month, int Day)
{
    public static readonly YearStart July1 = new(7, 1);
    public static readonly YearStart January1 = new(1, 1);

    public bool IsCalendarYear => Month == 1 && Day == 1;

    public string Text => new DateOnly(2001, Month, Day).ToString("d MMMM", CultureInfo.InvariantCulture);

    /// <summary>Days run to 28 so every year has the date.</summary>
    public static bool IsValid(int month, int day) => month is >= 1 and <= 12 && day >= 1 && day <= 28;
}

/// <summary>A country the site knows the usual settings for. Only Australia gets the Australian tax rules.</summary>
public sealed record Country(string Code, string Name, string Currency, YearStart YearStart, string Format);

/// <summary>A date and number format to choose from. All use a full stop for decimals, which the number boxes need.</summary>
public sealed record DisplayFormat(string Culture, string Name);

/// <summary>
/// A portfolio's country, financial year start, currency and date/number format. Anything not set
/// falls back to Australia, so existing portfolios carry on as before.
/// </summary>
public sealed record RegionSettings(string CountryCode, YearStart YearStart, string Currency, string Format)
{
    public const string AustraliaCode = "AU";

    public static readonly RegionSettings Australia = new(AustraliaCode, YearStart.July1, "AUD", "en-AU");

    public static readonly IReadOnlyList<Country> Countries =
    [
        new("AU", "Australia", "AUD", YearStart.July1, "en-AU"),
        new("NZ", "New Zealand", "NZD", new(4, 1), "en-NZ"),
        new("GB", "United Kingdom", "GBP", new(4, 6), "en-GB"),
        new("IE", "Ireland", "EUR", YearStart.January1, "en-IE"),
        new("US", "United States", "USD", YearStart.January1, "en-US"),
        new("CA", "Canada", "CAD", YearStart.January1, "en-CA"),
        new("SG", "Singapore", "SGD", YearStart.January1, "en-SG"),
        new("HK", "Hong Kong", "HKD", new(4, 1), "en-HK"),
        new("IN", "India", "INR", new(4, 1), "en-IN"),
        new("ZA", "South Africa", "ZAR", new(3, 1), "en-GB"),
        new("XX", "Other", "USD", YearStart.January1, "en-GB"),
    ];

    public static readonly IReadOnlyList<DisplayFormat> Formats =
    [
        new("en-AU", "Australia: 31/12/2026, $1,234.56"),
        new("en-NZ", "New Zealand: 31/12/2026, $1,234.56"),
        new("en-GB", "UK: 31/12/2026, 1,234.56"),
        new("en-IE", "Ireland: 31/12/2026, 1,234.56"),
        new("en-US", "US: 12/31/2026, 1,234.56"),
        new("en-CA", "Canada: 2026-12-31, 1,234.56"),
        new("en-SG", "Singapore: 31/12/2026, 1,234.56"),
        new("en-HK", "Hong Kong: 31/12/2026, 1,234.56"),
        new("en-IN", "India: 31/12/2026, 1,23,456.78"),
    ];

    public static readonly IReadOnlyList<string> Currencies =
        ["AUD", "NZD", "USD", "GBP", "EUR", "CAD", "SGD", "HKD", "JPY", "CHF", "INR", "ZAR", "CNY"];

    private static readonly Dictionary<string, string> Symbols = new()
    {
        ["AUD"] = "A$", ["NZD"] = "NZ$", ["USD"] = "US$", ["CAD"] = "C$", ["SGD"] = "S$", ["HKD"] = "HK$",
        ["GBP"] = "£", ["EUR"] = "€", ["JPY"] = "¥", ["CNY"] = "CN¥", ["INR"] = "₹", ["ZAR"] = "R", ["CHF"] = "CHF ",
    };

    private static readonly AsyncLocal<RegionSettings?> current = new();

    /// <summary>
    /// The settings for the portfolio being looked at in this request, set once per request like
    /// CultureInfo.CurrentCulture. Australia when nothing has set it (sign-in pages, tests, the price job).
    /// </summary>
    public static RegionSettings Current
    {
        get => current.Value ?? Australia;
        set => current.Value = value;
    }

    /// <summary>Franking credits, the CGT discount and the other Australian tax wording apply.</summary>
    public bool IsAustralia => CountryCode == AustraliaCode;

    public Country Country => FindCountry(CountryCode) ?? Countries[^1];

    public static Country? FindCountry(string? code) =>
        Countries.FirstOrDefault(c => c.Code.Equals(code?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Builds the settings from the stored columns, filling anything blank or unknown from the country (Australia if none).</summary>
    public static RegionSettings From(string? countryCode, int? yearStartMonth, int? yearStartDay, string? currency, string? format)
    {
        var country = FindCountry(countryCode) ?? Countries[0];
        var start = yearStartMonth is { } m && yearStartDay is { } d && YearStart.IsValid(m, d) ? new YearStart(m, d) : country.YearStart;
        var cur = currency?.Trim().ToUpperInvariant() is { Length: 3 } c ? c : country.Currency;
        var fmt = Formats.Any(f => f.Culture == format?.Trim()) ? format!.Trim() : country.Format;
        return new RegionSettings(country.Code, start, cur, fmt);
    }

    public static IReadOnlyList<string> Validate(string? countryCode, int month, int day, string? currency, string? format)
    {
        var errors = new List<string>();
        if (FindCountry(countryCode) is null)
            errors.Add("Choose a country.");
        if (!YearStart.IsValid(month, day))
            errors.Add("Choose the month the financial year starts in and a day from 1 to 28.");
        if (!Currencies.Contains(currency ?? ""))
            errors.Add("Choose a currency.");
        if (!Formats.Any(f => f.Culture == format))
            errors.Add("Choose a date and number format.");
        return errors;
    }

    /// <summary>
    /// The culture pages format with: the chosen date and number format, with the currency symbol of
    /// the portfolio's currency (the format's own symbol when it's that country's currency).
    /// </summary>
    public CultureInfo Culture()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo(Format).Clone();
        if (!string.Equals(new RegionInfo(Format).ISOCurrencySymbol, Currency, StringComparison.OrdinalIgnoreCase))
            culture.NumberFormat.CurrencySymbol = Symbols.TryGetValue(Currency, out var symbol) ? symbol : Currency + " ";
        return culture;
    }
}
