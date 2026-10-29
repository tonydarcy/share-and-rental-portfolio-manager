namespace PortfolioManager.Core.Import;

/// <summary>
/// Navexa "custom" holdings (unlisted funds, property, bullion and the like) have a free-text name for a symbol and the
/// exchange "CUSTOM". This site records them on one of its manually priced markets (FUND, PROPERTY, OTHER) under a short code.
/// </summary>
public static class NavexaCustomAssets
{
    public const int MaxCodeLength = 20;

    public static bool IsCustomExchange(string? exchange) => string.Equals(exchange?.Trim(), "CUSTOM", StringComparison.OrdinalIgnoreCase);

    /// <summary>FUND for a fund or trust, PROPERTY for property, otherwise OTHER (bullion, collectables, anything else).</summary>
    public static string Market(string name)
    {
        var words = Words(name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (words.Overlaps(["FUND", "FUNDS", "TRUST"]))
            return "FUND";
        if (words.Overlaps(["PROPERTY", "PROPERTIES", "HOUSE", "APARTMENT", "LAND", "REALESTATE"]))
            return "PROPERTY";
        return "OTHER";
    }

    /// <summary>
    /// A short code: a code-like word after the colon if there is one ("Brickx Managed Fund: BXM01 - Secured Mortgages" is BXM01),
    /// otherwise the first word and the initials of the rest ("ABC Bullion: Gold (Gold Saver)" is GOLD-GS).
    /// </summary>
    public static string Code(string name)
    {
        var s = name.Trim();
        var colon = s.IndexOf(':');
        if (colon >= 0 && colon < s.Length - 1)
            s = s[(colon + 1)..];
        var words = Words(s).ToList();
        if (words.Count == 0)
            return "";
        var first = words[0];
        if (words.Count == 1 || (first.Any(char.IsAsciiDigit) && first.Any(char.IsAsciiLetter)))
            return Cut(first);
        return Cut(first + "-" + string.Concat(words.Skip(1).Select(w => w[0])));
    }

    /// <summary>A code for each holding (keyed by id), with -2, -3 added where two different holdings would share one.</summary>
    public static Dictionary<string, string> Codes(IEnumerable<(string Id, string Name)> holdings)
    {
        var result = new Dictionary<string, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in holdings.DistinctBy(h => h.Id).OrderBy(h => h.Id.Length).ThenBy(h => h.Id, StringComparer.Ordinal))
        {
            var code = Code(name);
            if (code.Length == 0)
                continue;
            var candidate = code;
            for (var n = 2; !used.Add(candidate); n++)
                candidate = Cut(code, MaxCodeLength - $"-{n}".Length) + $"-{n}";
            result[id] = candidate;
        }
        return result;
    }

    private static IEnumerable<string> Words(string text) =>
        text.Split((char[])[' ', '-', '(', ')', ',', '/', '&', '.', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant())
            .Where(w => w.Length > 0);

    private static string Cut(string s, int max = MaxCodeLength) => s.Length <= max ? s : s[..max];
}
