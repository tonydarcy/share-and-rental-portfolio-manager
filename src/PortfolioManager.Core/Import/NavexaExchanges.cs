namespace PortfolioManager.Core.Import;

public sealed record ExchangeMatch(string Code, string? Currency, bool Recognised);

/// <summary>Maps the exchange names and codes Navexa may export to the codes this site stores.</summary>
public static class NavexaExchanges
{
    private static readonly (string Code, string Currency, string[] Names)[] Known =
    [
        // Chi-X / Cboe Australia trades are in ASX-listed securities, which this site prices under ASX
        ("ASX", "AUD", ["ASX", "AXW", "AX", "AU", "XASX", "AUSTRALIAN SECURITIES EXCHANGE", "CXA", "CHI-X", "CHIX", "CBOE AUSTRALIA"]),
        ("NSX", "AUD", ["NSX", "NATIONAL STOCK EXCHANGE OF AUSTRALIA"]),
        ("NZX", "NZD", ["NZX", "NZE", "NZ", "XNZE", "NEW ZEALAND STOCK EXCHANGE"]),
        ("NASDAQ", "USD", ["NASDAQ", "NAS", "NMS", "NGM", "NCM", "NSQ", "XNAS"]),
        ("NYSE", "USD", ["NYSE", "NYQ", "NYS", "XNYS", "NEW YORK STOCK EXCHANGE"]),
        ("NYSEARCA", "USD", ["NYSEARCA", "NYSE ARCA", "ARCA", "PCX", "ARCX"]),
        ("AMEX", "USD", ["AMEX", "ASE", "NYSE AMERICAN", "NYSEAMERICAN", "NYSE MKT"]),
        ("BATS", "USD", ["BATS", "BZX", "CBOE BZX"]),
        ("LSE", "GBP", ["LSE", "LON", "XLON", "LONDON STOCK EXCHANGE"]),
        ("TSX", "CAD", ["TSX", "TOR", "XTSE", "TORONTO STOCK EXCHANGE"]),
        ("HKEX", "HKD", ["HKEX", "HKG", "HKSE", "XHKG", "HONG KONG STOCK EXCHANGE"]),
        ("SGX", "SGD", ["SGX", "SES", "XSES", "SINGAPORE EXCHANGE"]),
    ];

    private static readonly Dictionary<string, (string Code, string Currency)> ByName =
        Known.SelectMany(k => k.Names.Select(n => (n, k.Code, k.Currency)))
             .ToDictionary(x => x.n, x => (x.Code, x.Currency), StringComparer.OrdinalIgnoreCase);

    /// <summary>Navexa's "Crypto" exchange. Coins have no usual currency: each is priced in the currency of its trades.</summary>
    private static readonly HashSet<string> CryptoNames = new(["CRYPTO", "CRYPTOCURRENCY", "CC"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Unknown names come back upper-cased with Recognised = false so the preview can flag them.</summary>
    public static ExchangeMatch Map(string exchange)
    {
        var key = exchange.Trim();
        if (CryptoNames.Contains(key))
            return new ExchangeMatch(Crypto.CryptoCodes.Market, null, true);
        if (ByName.TryGetValue(key, out var hit))
            return new ExchangeMatch(hit.Code, hit.Currency, true);
        return new ExchangeMatch(key.ToUpperInvariant(), null, false);
    }

    /// <summary>
    /// Splits symbols such as "CBA.AX" or "ASX:CBA" into exchange and code.
    /// Only known Australian/NZ suffixes are stripped, so codes like BRK.B are left alone.
    /// </summary>
    public static (string? Exchange, string Code) SplitSymbol(string symbol)
    {
        var s = symbol.Trim();
        var colon = s.IndexOf(':');
        if (colon > 0 && ByName.ContainsKey(s[..colon]))
            return (s[..colon], s[(colon + 1)..].Trim());

        foreach (var (suffix, exchange) in new[] { (".AX", "ASX"), (".ASX", "ASX"), (".NZ", "NZX") })
        {
            if (s.Length > suffix.Length && s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return (exchange, s[..^suffix.Length]);
        }
        return (null, s);
    }
}
