namespace PortfolioManager.Core.Crypto;

/// <summary>
/// Crypto is held as trades on market CRYPTO with codes the price provider knows: the coin and the currency it's
/// priced in, such as BTC-AUD (EODHD's BTC-AUD.CC). Crypto never pays franked dividends.
/// </summary>
public static class CryptoCodes
{
    public const string Market = "CRYPTO";

    public const string FrankingProblem = "Crypto doesn't pay franked dividends, so it can't have a franked amount or a franking credit.";

    public static bool IsCrypto(string? market) => string.Equals(market?.Trim(), Market, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The code to record a coin under: BTC in an AUD portfolio is BTC-AUD. A pair written BTC/AUD or BTC-AUD is kept
    /// as it is (with a dash), so a coin priced in another currency stays priced in that one.
    /// </summary>
    public static string SiteCode(string coin, string currency)
    {
        var s = new string(coin.Trim().ToUpperInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace('/', '-');
        if (s.Length == 0)
            return s;
        if (s.Contains('-'))
            return s;
        return $"{s}-{currency.Trim().ToUpperInvariant()}";
    }

    /// <summary>The coin part of a code: BTC-AUD is BTC.</summary>
    public static string Coin(string code)
    {
        var s = code.Trim().ToUpperInvariant();
        var dash = s.IndexOf('-');
        return dash > 0 ? s[..dash] : s;
    }

    /// <summary>The franking check for a payout or DRP on any market; null when it's fine.</summary>
    public static string? CheckNoFranking(string? market, decimal franked, decimal frankingCredit) =>
        IsCrypto(market) && (franked != 0 || frankingCredit != 0) ? FrankingProblem : null;
}
