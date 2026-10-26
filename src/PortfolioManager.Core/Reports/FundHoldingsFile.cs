using System.Globalization;
using PortfolioManager.Core.Imports;

namespace PortfolioManager.Core.Reports;

/// <summary>What was read from a fund issuer's holdings file: the look-through weights, and how many lines went into them.</summary>
public sealed record FundHoldingsRead(IReadOnlyList<ExposureWeight> Weights, int Lines, IReadOnlyList<string> Warnings, string? Error = null)
{
    public static FundHoldingsRead Failed(string error) => new([], 0, [], error);
}

/// <summary>
/// Reads the full holdings file a fund issuer publishes for an ETF or managed fund (Vanguard, iShares, BetaShares, SPDR,
/// VanEck and the like) into look-through weights: the sectors and countries added up over every line, and the largest
/// holdings by name. Issuers lay their files out differently, so the header row is found by its column names, and any
/// lines about the fund above it are skipped. Each line's weight comes from its weight column, or its share of the
/// file's total market value when there is no weight column.
/// </summary>
public static class FundHoldingsFile
{
    /// <summary>How many of the largest holdings are kept by name; the rest show as "Rest of the fund".</summary>
    public const int TopHoldings = 25;

    private static readonly string[] CodeColumns = ["ticker", "code", "asx code", "security code", "symbol", "issuer ticker", "ticker symbol", "local ticker"];
    private static readonly string[] NameColumns = ["name", "security name", "holding", "holding name", "holdings", "security", "issuer name", "security description", "description", "company"];
    private static readonly string[] WeightColumns = ["weight (%)", "weight", "% of net assets", "% of fund", "% of net asset value", "weighting", "% weight", "weight %",
        "portfolio weight", "market value %", "% of market value", "% of portfolio", "percent of fund", "% net assets", "fund weight (%)", "weight(%)"];
    private static readonly string[] ValueColumns = ["market value", "market value (aud)", "market value aud", "market value ($)", "value (aud)", "value", "notional value", "market value ($aud)"];
    private static readonly string[] SectorColumns = ["sector", "gics sector", "industry sector", "sector classification", "gics sector name"];
    private static readonly string[] CountryColumns = ["country", "location", "country of risk", "location of risk", "domicile", "country of domicile", "region"];
    private static readonly string[] AssetClassColumns = ["asset class", "security type", "asset type", "type"];

    public static FundHoldingsRead Parse(string text)
    {
        var lines = CsvImport.Lines(text).ToList();
        if (lines.Count > CsvImport.MaxLines)
            return FundHoldingsRead.Failed($"The file has more than {CsvImport.MaxLines:N0} lines.");

        // The header is the first line with a name (or code) column and a weight or market value column
        int header = -1;
        IReadOnlyList<string> names = [];
        for (var i = 0; i < Math.Min(lines.Count, 40) && header < 0; i++)
        {
            var f = CsvImport.Split(lines[i].Text).Select(Clean).ToList();
            if ((Find(f, NameColumns) >= 0 || Find(f, CodeColumns) >= 0) && (Find(f, WeightColumns) >= 0 || Find(f, ValueColumns) >= 0))
                (header, names) = (i, f);
        }
        if (header < 0)
            return FundHoldingsRead.Failed("Couldn't find the header row. The file needs a column for each holding's name or code, and one for its weight (%) or market value.");

        int code = Find(names, CodeColumns), name = Find(names, NameColumns), weight = Find(names, WeightColumns), value = Find(names, ValueColumns),
            sector = Find(names, SectorColumns), country = Find(names, CountryColumns), assetClass = Find(names, AssetClassColumns);
        var warnings = new List<string>();
        var rows = new List<(string Code, string? Name, decimal Amount, string? Sector, string? Country)>();
        foreach (var (number, line) in lines.Skip(header + 1))
        {
            var f = CsvImport.Split(line);
            string Field(int i) => i >= 0 && i < f.Count ? f[i].Trim() : "";
            var amountText = weight >= 0 ? Field(weight) : Field(value);
            if (Field(code).Length == 0 && Field(name).Length == 0)
                continue;
            if (!TryNumber(amountText, out var amount))
            {
                // Issuers put notes and totals below the holdings; stop at the first line that isn't a holding
                if (rows.Count > 0 && f.Count(x => x.Length > 0) <= 2)
                    break;
                continue;
            }
            if (amount == 0)
                continue;
            var isCash = Field(assetClass).Contains("cash", StringComparison.OrdinalIgnoreCase)
                || Field(sector).Contains("cash", StringComparison.OrdinalIgnoreCase);
            rows.Add((Ticker(Field(code)), Field(name).Length > 0 ? Field(name) : null, amount,
                isCash ? "Cash" : Field(sector) is { Length: > 0 } s && s != "-" ? LookThrough.SectorName(s) : null,
                Field(country) is { Length: > 0 } c && c != "-" ? c : null));
        }
        if (rows.Count == 0)
            return FundHoldingsRead.Failed("The file has no holdings under its header row.");

        // Weights as fractions of the fund: a weight column in % (or already as fractions), or each market value's share
        var total = rows.Sum(r => r.Amount);
        if (total <= 0)
            return FundHoldingsRead.Failed("The weights in the file add up to nothing.");
        decimal scale = weight < 0 ? 1 / total : total <= 1.05m ? 1 : 0.01m;
        if (weight >= 0 && Math.Abs(total * scale - 1) > 0.05m)
            warnings.Add($"The weights in the file add up to {total * scale:P1}, not 100%; they're used as they are.");

        var weights = new List<ExposureWeight>();
        if (sector >= 0 || rows.Any(r => r.Sector == "Cash"))
            weights.AddRange(Group(rows.Where(r => r.Sector is not null), r => r.Sector!, scale, LookThrough.Sector));
        else
            warnings.Add("The file has no sector column, so the sector look-through isn't filled in.");
        if (country >= 0)
            weights.AddRange(Group(rows.Where(r => r.Country is not null), r => r.Country!, scale, LookThrough.Country));
        else
            warnings.Add("The file has no country column, so the country look-through isn't filled in.");
        weights.AddRange(rows.Where(r => r.Sector != "Cash")
            .GroupBy(r => r.Code.Length > 0 ? r.Code : r.Name!.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new ExposureWeight(LookThrough.Holding, Truncate(g.Key, 50), Weight(g.Sum(r => r.Amount) * scale),
                g.Select(r => r.Name).FirstOrDefault(n => n is not null) is { } n && g.Key != n.ToUpperInvariant() ? Truncate(n, 100) : null))
            .Where(w => w.Weight > 0)
            .OrderByDescending(w => w.Weight).Take(TopHoldings));
        return new FundHoldingsRead(weights, rows.Count, warnings);
    }

    private static IEnumerable<ExposureWeight> Group(IEnumerable<(string Code, string? Name, decimal Amount, string? Sector, string? Country)> rows,
        Func<(string Code, string? Name, decimal Amount, string? Sector, string? Country), string> key, decimal scale, string kind) =>
        rows.GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ExposureWeight(kind, Truncate(g.Key, 100), Weight(g.Sum(r => r.Amount) * scale)))
            .Where(w => w.Weight > 0)
            .OrderByDescending(w => w.Weight);

    // Weights are kept to 0.01% (four places as a fraction), which is what can be entered by hand
    private static decimal Weight(decimal fraction) => Math.Round(Math.Clamp(fraction, 0, 1), 4, MidpointRounding.AwayFromZero);

    private static string Clean(string header) => header.Trim().Trim('"').Replace(' ', ' ').ToLowerInvariant();

    private static int Find(IReadOnlyList<string> headers, string[] wanted)
    {
        foreach (var w in wanted)
        {
            var i = headers.ToList().FindIndex(h => h == w);
            if (i >= 0)
                return i;
        }
        return -1;
    }

    /// <summary>"CBA AU", "CBA.AX" and "ASX:CBA" are all CBA.</summary>
    public static string Ticker(string code)
    {
        var c = code.Trim().ToUpperInvariant();
        if (c.Contains(':'))
            c = c[(c.LastIndexOf(':') + 1)..];
        foreach (var suffix in new[] { " AU", " AT", ".AX", ".ASX", " US", " UW", " UN", " LN", " NZ" })
            if (c.EndsWith(suffix, StringComparison.Ordinal))
                c = c[..^suffix.Length];
        return c == "-" ? "" : c.Trim();
    }

    private static bool TryNumber(string s, out decimal value)
    {
        var t = s.Trim().TrimEnd('%').Replace("A$", "").Replace("$", "").Trim();
        var negative = t.StartsWith('(') && t.EndsWith(')');
        if (negative)
            t = t[1..^1];
        var ok = decimal.TryParse(t, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
        if (negative)
            value = -value;
        return ok;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
