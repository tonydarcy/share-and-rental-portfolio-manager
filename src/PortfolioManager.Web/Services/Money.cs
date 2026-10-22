using System.Globalization;

namespace PortfolioManager.Web.Services;

/// <summary>Amounts in the portfolio's currency use its format; another currency's amounts show its code (USD 1,234.50).</summary>
public static class Money
{
    public static string Format(decimal amount, string? currency) =>
        currency is null || string.Equals(currency, Core.Regions.RegionSettings.Current.Currency, StringComparison.OrdinalIgnoreCase)
            ? amount.ToString("C")
            : $"{currency} {amount.ToString("N2", CultureInfo.CurrentCulture)}";
}
