using System.Data;
using System.Globalization;
using System.Xml.Linq;
using Dapper;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Holdings;

namespace PortfolioManager.Core.Reports;

/// <summary>One line of what is inside a fund: a sector, a country (or region) or one of its largest holdings. Weight 0.25 is 25%.</summary>
public sealed record ExposureWeight(string Kind, string Name, decimal Weight, string? Label = null);

/// <summary>A fund's look-through, either the portfolio's own entry or the shared one from the price updater.</summary>
public sealed record FundExposure(string ExchangeCode, string StockCode, IReadOnlyList<ExposureWeight> Weights, DateOnly? AsAt, string? Source, bool Own);

/// <summary>A holding as the look-through needs it: its value now, and its own sector when it's a company.</summary>
public sealed record LookThroughHolding(string ExchangeCode, string StockCode, decimal Value, string? Sector);

/// <summary>An amount of the portfolio exposed to one sector, country or company, and how much of it comes through funds.</summary>
public sealed record ExposureLine(string Name, string? Label, decimal Value, decimal ThroughFunds, decimal Share);

public sealed record LookThroughReport(decimal Total, IReadOnlyList<ExposureLine> Sectors, IReadOnlyList<ExposureLine> Countries,
    IReadOnlyList<ExposureLine> Companies, IReadOnlyList<string> FundsCovered, IReadOnlyList<string> FundsWithoutData);

public static class LookThrough
{
    public const string Sector = "Sector", Country = "Country", Holding = "Holding", None = "None";
    public const string Unknown = "Unknown", RestOfFund = "Rest of the fund";
    public static readonly string[] Kinds = [Sector, Country, Holding];

    /// <summary>Codes that are always funds (manual-priced managed funds), so a missing look-through is pointed out.</summary>
    public static bool LooksLikeFund(string exchangeCode) => exchangeCode.Equals("FUND", StringComparison.OrdinalIgnoreCase);

    /// <summary>The country of a company from the market it's listed on, when the market is in one country.</summary>
    public static string CountryOf(string exchangeCode) => exchangeCode.Trim().ToUpperInvariant() switch
    {
        "ASX" or "AU" or "CXA" or "NSX" => "Australia",
        "NZX" or "NZ" or "NZE" => "New Zealand",
        "NASDAQ" or "NYSE" or "NYSEARCA" or "AMEX" or "US" or "BATS" or "OTC" => "United States",
        "LSE" or "LON" or "UK" => "United Kingdom",
        "TSX" or "TSXV" or "TO" or "V" => "Canada",
        "NSE" or "BSE" => "India",
        "HKEX" or "HK" => "Hong Kong",
        "SGX" or "SG" => "Singapore",
        _ => Unknown,
    };

    /// <summary>Provider sector names (Morningstar style) as the GICS names the share details use.</summary>
    public static string SectorName(string? name)
    {
        var n = (name ?? "").Trim();
        return n.ToLowerInvariant() switch
        {
            "" => Unknown,
            "financial services" or "financial" => "Financials",
            "basic materials" => "Materials",
            "consumer cyclical" or "consumer cyclicals" => "Consumer Discretionary",
            "consumer defensive" or "consumer non-cyclicals" => "Consumer Staples",
            "technology" or "information technology" => "Information Technology",
            "healthcare" or "health care" => "Health Care",
            "communication services" or "communication" or "telecommunications" => "Communication Services",
            "realestate" or "real estate" => "Real Estate",
            _ => n,
        };
    }

    /// <summary>
    /// Spreads each fund's value over what's inside it (anything its weights don't cover goes to Unknown, or "Rest of the
    /// fund" for its holdings), and counts each company directly. A company held directly and through funds is one line.
    /// </summary>
    public static LookThroughReport Build(IReadOnlyList<LookThroughHolding> holdings, IReadOnlyDictionary<(string, string), FundExposure> funds)
    {
        var total = holdings.Sum(h => h.Value);
        var sectors = new Dictionary<string, (decimal Value, decimal Funds)>(StringComparer.OrdinalIgnoreCase);
        var countries = new Dictionary<string, (decimal Value, decimal Funds)>(StringComparer.OrdinalIgnoreCase);
        var companies = new Dictionary<string, (string? Label, decimal Value, decimal Funds)>(StringComparer.OrdinalIgnoreCase);
        var covered = new List<string>();
        var without = new List<string>();

        static void Add(Dictionary<string, (decimal, decimal)> map, string name, decimal value, bool fund)
        {
            var (v, f) = map.GetValueOrDefault(name);
            map[name] = (v + value, f + (fund ? value : 0));
        }

        foreach (var h in holdings.Where(h => h.Value != 0))
        {
            var key = (h.ExchangeCode.Trim().ToUpperInvariant(), h.StockCode.Trim().ToUpperInvariant());
            if (funds.TryGetValue(key, out var fund) && fund.Weights.Any(w => w.Kind != None))
            {
                covered.Add(key.Item2);
                foreach (var kind in Kinds)
                {
                    var lines = fund.Weights.Where(w => w.Kind == kind).ToList();
                    var rest = h.Value * (1 - Math.Min(1, lines.Sum(w => w.Weight)));
                    foreach (var w in lines)
                    {
                        var part = h.Value * w.Weight;
                        if (kind == Sector) Add(sectors, SectorName(w.Name), part, true);
                        else if (kind == Country) Add(countries, w.Name, part, true);
                        else
                        {
                            var (label, v, f) = companies.GetValueOrDefault(w.Name.ToUpperInvariant());
                            companies[w.Name.ToUpperInvariant()] = (label ?? w.Label, v + part, f + part);
                        }
                    }
                    if (rest > 0.005m)
                    {
                        if (kind == Sector) Add(sectors, Unknown, rest, true);
                        else if (kind == Country) Add(countries, Unknown, rest, true);
                        else
                        {
                            var (label, v, f) = companies.GetValueOrDefault(RestOfFund);
                            companies[RestOfFund] = (label, v + rest, f + rest);
                        }
                    }
                }
                continue;
            }

            if (LooksLikeFund(key.Item1))
                without.Add(key.Item2);
            Add(sectors, SectorName(h.Sector), h.Value, false);
            Add(countries, CountryOf(key.Item1), h.Value, false);
            var (l, cv, cf) = companies.GetValueOrDefault(key.Item2);
            companies[key.Item2] = (l, cv + h.Value, cf);
        }

        decimal Share(decimal v) => total == 0 ? 0 : v / total;
        List<ExposureLine> Lines(IEnumerable<ExposureLine> lines) =>
            lines.OrderBy(x => x.Name is Unknown or RestOfFund ? 1 : 0).ThenByDescending(x => x.Value).ToList();
        return new LookThroughReport(total,
            Lines(sectors.Select(s => new ExposureLine(s.Key, null, Round(s.Value.Value), Round(s.Value.Funds), Share(s.Value.Value)))),
            Lines(countries.Select(s => new ExposureLine(s.Key, null, Round(s.Value.Value), Round(s.Value.Funds), Share(s.Value.Value)))),
            Lines(companies.Select(s => new ExposureLine(s.Key, s.Value.Label, Round(s.Value.Value), Round(s.Value.Funds), Share(s.Value.Value)))),
            covered, without);
    }

    /// <summary>
    /// Reads pasted lines of "kind, name, weight %" with an optional company name after a holding, for example
    /// "Sector, Financials, 31.5" or "Holding, CBA, 10.2, Commonwealth Bank". A header line is skipped.
    /// </summary>
    public static (IReadOnlyList<ExposureWeight> Weights, IReadOnlyList<string> Errors) Parse(string? text)
    {
        var weights = new List<ExposureWeight>();
        var errors = new List<string>();
        var n = 0;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            n++;
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var parts = line.Split(line.Contains('\t') ? '\t' : ',', StringSplitOptions.TrimEntries);
            var kind = Kinds.FirstOrDefault(k => k.Equals(parts[0], StringComparison.OrdinalIgnoreCase)
                || (k == Country && parts[0].Equals("Region", StringComparison.OrdinalIgnoreCase)));
            if (kind is null)
            {
                if (n > 1 || !parts[0].Equals("Kind", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Line {n}: start with Sector, Country or Holding.");
                continue;
            }
            if (parts.Length < 3 || parts[1].Length == 0
                || !decimal.TryParse(parts[2].TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out var pct) || pct < 0 || pct > 100)
            {
                errors.Add($"Line {n}: give a name and a weight from 0 to 100.");
                continue;
            }
            weights.Add(new ExposureWeight(kind, kind == Holding ? parts[1].ToUpperInvariant() : parts[1], pct / 100,
                kind == Holding && parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null));
        }
        foreach (var g in weights.GroupBy(w => w.Kind).Where(g => g.Sum(w => w.Weight) > 1.02m))
            errors.Add($"The {g.Key.ToLowerInvariant()} weights add up to {g.Sum(w => w.Weight):P1}, more than 100%.");
        foreach (var g in weights.GroupBy(w => (w.Kind, w.Name.ToUpperInvariant())).Where(g => g.Count() > 1))
            errors.Add($"{g.Key.Item2} is listed twice.");
        return (weights, errors);
    }

    public static string WeightsXml(IEnumerable<ExposureWeight> weights) =>
        new XElement("w", weights.Select(w => new XElement("e",
            new XAttribute("k", w.Kind), new XAttribute("n", w.Name), new XAttribute("l", w.Label ?? ""),
            new XAttribute("v", w.Weight.ToString(CultureInfo.InvariantCulture))))).ToString(SaveOptions.DisableFormatting);

    public static string ToText(IEnumerable<ExposureWeight> weights) =>
        string.Join("\n", weights.Where(w => w.Kind != None).Select(w =>
            $"{w.Kind}, {w.Name}, {(w.Weight * 100).ToString("0.##", CultureInfo.InvariantCulture)}" + (w.Label is null ? "" : $", {w.Label}")));

    private static decimal Round(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Reads and saves look-through (tblFundExposure, through 2026-10-21-06-fund-look-through.sql) and builds the report.</summary>
public sealed class LookThroughService(IDbConnectionFactory db, HoldingsService holdings)
{
    private sealed record Row(int? UserId, string Exchange, string Code, string Kind, string Name, string? Label, double Weight, DateTime? AsAt, string? Source);

    /// <summary>Each fund's look-through for this portfolio: its own entry where there is one, otherwise the shared one.</summary>
    public async Task<IReadOnlyDictionary<(string, string), FundExposure>> GetFundsAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<Row>("""
            SELECT iUserID AS UserId, strStockExchangeCode AS Exchange, strStockCode AS Code, strKind AS Kind, strName AS Name,
                   strLabel AS Label, dblWeight AS Weight, CAST(dtmAsAt AS datetime) AS AsAt, strSource AS Source
            FROM tblFundExposure
            WHERE (iUserID IS NULL OR iUserID = @portfolioId)
              AND EXISTS (SELECT 1 FROM tblShareTrades T WHERE T.iUserId = @portfolioId
                          AND T.strStockExchangeCode = tblFundExposure.strStockExchangeCode AND T.strStockCode = tblFundExposure.strStockCode)
            """, new { portfolioId });
        return rows.GroupBy(r => (r.Exchange.Trim().ToUpperInvariant(), r.Code.Trim().ToUpperInvariant()))
            .ToDictionary(g => g.Key, g =>
            {
                var own = g.Any(r => r.UserId == portfolioId);
                var use = g.Where(r => own ? r.UserId == portfolioId : r.UserId is null).ToList();
                return new FundExposure(g.Key.Item1, g.Key.Item2,
                    use.Select(r => new ExposureWeight(r.Kind, r.Name, (decimal)r.Weight, r.Label)).ToList(),
                    use.Select(r => r.AsAt).FirstOrDefault() is { } d ? DateOnly.FromDateTime(d) : null, use.FirstOrDefault()?.Source, own);
            });
    }

    public async Task<LookThroughReport> GetAsync(int portfolioId)
    {
        var held = await holdings.GetHoldingsAsync(portfolioId, null, true);
        await using var con = db.Create();
        var sectors = (await con.QueryAsync<(string Exchange, string Code, string? Sector)>(
                "SELECT strStockExchangeCode, strStockCode, strSector FROM tblShareDetails D WHERE EXISTS (SELECT 1 FROM tblShareTrades T " +
                "WHERE T.iUserId = @portfolioId AND T.strStockExchangeCode = D.strStockExchangeCode AND T.strStockCode = D.strStockCode)",
                new { portfolioId }))
            .GroupBy(s => (s.Exchange.Trim().ToUpperInvariant(), s.Code.Trim().ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.Select(s => s.Sector).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)));
        var input = held.Holdings.Where(h => h.Units != 0)
            .Select(h => new LookThroughHolding(h.ExchangeCode, h.StockCode, h.MarketValue,
                sectors.GetValueOrDefault((h.ExchangeCode.Trim().ToUpperInvariant(), h.StockCode.Trim().ToUpperInvariant()))))
            .ToList();
        return LookThrough.Build(input, await GetFundsAsync(portfolioId));
    }

    /// <summary>Saves the portfolio's own look-through for a fund it holds (or has held).</summary>
    public async Task<ProcResult> SaveAsync(int portfolioId, string exchangeCode, string stockCode, IReadOnlyList<ExposureWeight> weights, DateOnly? asAt)
    {
        await using var con = db.Create();
        var held = await con.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tblShareTrades WHERE iUserId = @portfolioId AND strStockExchangeCode = @exchangeCode AND strStockCode = @stockCode",
            new { portfolioId, exchangeCode, stockCode });
        if (held == 0)
            return ProcResult.Failed("That fund isn't in this portfolio.");
        var result = await con.ExecuteScalarAsync<string>("uspSaveFundExposure", new
        {
            iUserID = portfolioId, strStockExchangeCode = exchangeCode, strStockCode = stockCode,
            dtmAsAt = asAt?.ToDateTime(TimeOnly.MinValue), strSource = "Entered", xmlWeights = LookThrough.WeightsXml(weights),
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    public async Task<ProcResult> DeleteAsync(int portfolioId, string exchangeCode, string stockCode)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspDeleteFundExposure",
            new { iUserID = portfolioId, strStockExchangeCode = exchangeCode, strStockCode = stockCode }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }
}

/// <summary>
/// For the price updater: asks the provider what is inside each held stock (once every <see cref="RefreshDays"/> days)
/// and saves it as the shared look-through. Stocks that aren't funds are noted, so they're only asked again after that.
/// </summary>
public sealed class FundExposureUpdateService(IDbConnectionFactory db, Prices.EodhdPriceProvider provider)
{
    public const int RefreshDays = 30;

    public async Task<(int Checked, int Funds, IReadOnlyList<string> Errors)> RunAsync(DateOnly today, Action<string>? log = null, CancellationToken ct = default)
    {
        log ??= _ => { };
        var held = await new Prices.AnnouncementStore(db).GetHeldStocksAsync(ct);
        await using var con = db.Create();
        var fresh = (await con.QueryAsync<(string Exchange, string Code)>(
                "SELECT DISTINCT strStockExchangeCode, strStockCode FROM tblFundExposure WHERE iUserID IS NULL AND dtmSaved >= @since",
                new { since = today.AddDays(-RefreshDays).ToDateTime(TimeOnly.MinValue) }))
            .Select(r => (r.Exchange.Trim().ToUpperInvariant(), r.Code.Trim().ToUpperInvariant())).ToHashSet();
        var due = held.Where(s => !fresh.Contains((s.ExchangeCode, s.StockCode))).ToList();
        log($"{due.Count} of {held.Count} held stocks to check for fund look-through.");

        var errors = new List<string>();
        var funds = 0;
        foreach (var symbol in due)
        {
            var result = await provider.GetFundExposureAsync(symbol, ct);
            if (!result.IsOk)
            {
                errors.Add($"{symbol} look-through: {result.Message}");
                if (result.StopsRun) break;
                continue;
            }
            var weights = result.Items.Count > 0 ? result.Items : [new ExposureWeight(LookThrough.None, "-", 0)];
            var saved = await con.ExecuteScalarAsync<string>("uspSaveFundExposure", new
            {
                iUserID = (int?)null, strStockExchangeCode = symbol.ExchangeCode, strStockCode = symbol.StockCode,
                dtmAsAt = today.ToDateTime(TimeOnly.MinValue), strSource = provider.Name, xmlWeights = LookThrough.WeightsXml(weights),
            }, commandType: CommandType.StoredProcedure);
            if (saved?.StartsWith("Error", StringComparison.OrdinalIgnoreCase) == true)
                errors.Add($"{symbol} look-through: {saved}");
            else if (result.Items.Count > 0)
                funds++;
        }
        foreach (var e in errors)
            log(e);
        log($"Saved look-through for {funds} fund(s).");
        return (due.Count, funds, errors);
    }
}
