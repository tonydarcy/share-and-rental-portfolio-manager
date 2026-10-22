using System.Data;
using System.Globalization;
using Dapper;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Imports;

namespace PortfolioManager.Core.Prices;

/// <summary>A kind of asset and the market code its trades use.</summary>
public sealed record AssetType(string Market, string Name, string Pricing);

/// <summary>
/// Assets other than listed shares. They are trades like any other (so CGT, the ledger and the reports work as they do
/// for shares), on a market code that says what they are. Crypto prices come from the price provider (EODHD's CC feed,
/// codes like BTC-AUD); the rest are priced by hand or from a CSV for each portfolio.
/// </summary>
public static class AssetTypes
{
    public static readonly IReadOnlyList<AssetType> All =
    [
        new("FUND", "Managed fund (unlisted)", "Enter the unit price from the fund's statement or website."),
        new("CRYPTO", "Crypto", "Daily prices from the price provider; use codes like BTC-AUD or ETH-AUD."),
        new("UNLISTED", "Unlisted shares", "Enter a valuation when you have one."),
        new("PROPERTY", "Property", "Record it as 1 unit at the full cost; enter a valuation as the price."),
        new("OTHER", "Other asset", "Enter a price or valuation by hand."),
    ];

    /// <summary>Markets with no price feed: priced on Prices > Manual prices and skipped by the price updater.</summary>
    public static readonly IReadOnlySet<string> ManualMarkets = new HashSet<string>(["FUND", "UNLISTED", "PROPERTY", "OTHER"], StringComparer.OrdinalIgnoreCase);

    public static bool IsManual(string market) => ManualMarkets.Contains(market.Trim());

    public static string Describe(string market) =>
        All.FirstOrDefault(a => a.Market.Equals(market.Trim(), StringComparison.OrdinalIgnoreCase))?.Name ?? "Listed share or ETF";
}

public sealed record ManualPrice(int PriceId, string ExchangeCode, string StockCode, DateOnly Date, decimal Price);

public sealed record ManualPriceRow(string StockCode, DateOnly Date, decimal Price);

/// <summary>A portfolio's own prices (tblSharePrice rows with its iUserID), through the procs in 2026-10-21-04-manual-prices.sql.</summary>
public sealed class ManualPriceService(IDbConnectionFactory db)
{
    private sealed record Row(int PriceId, string ExchangeCode, string StockCode, DateTime Date, double Price);

    public async Task<IReadOnlyList<ManualPrice>> GetAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<Row>("uspGetManualPrices", new { iUserID = portfolioId }, commandType: CommandType.StoredProcedure);
        return rows.Select(r => new ManualPrice(r.PriceId, r.ExchangeCode.Trim(), r.StockCode.Trim(), DateOnly.FromDateTime(r.Date), (decimal)r.Price)).ToList();
    }

    public async Task<ProcResult> SaveAsync(int portfolioId, string market, string code, DateOnly date, decimal price)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspSaveManualPrice", new
        {
            iUserID = portfolioId,
            strStockExchangeCode = market.Trim().ToUpperInvariant(),
            strStockCode = code.Trim().ToUpperInvariant(),
            dtmDate = date.ToDateTime(TimeOnly.MinValue),
            dblClosePrice = (double)price,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    public async Task<ProcResult> DeleteAsync(int portfolioId, int priceId)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspDeleteManualPrice", new { iUserID = portfolioId, iSharePriceId = priceId },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    /// <summary>A CSV of Code, Date, Price (a header line is skipped), for one market.</summary>
    public static ImportPreview<ManualPriceRow> Parse(string text, DateOnly today)
    {
        var lines = new List<ParsedLine<ManualPriceRow>>();
        foreach (var (number, line) in CsvImport.Lines(text))
        {
            if (lines.Count >= CsvImport.MaxLines)
                return ImportPreview<ManualPriceRow>.Failed($"The file has more than {CsvImport.MaxLines:N0} lines; split it into smaller files.");
            var f = CsvImport.Split(line);
            if (lines.Count == 0 && f.Count >= 3 && !BrokerCsv.TryAmount(f[2], out _))
                continue; // header
            var errors = new List<string>();
            if (f.Count < 3)
                errors.Add("Expected Code, Date, Price.");
            var code = f.Count > 0 ? f[0].Trim().ToUpperInvariant() : "";
            if (code.Length is 0 or > 50)
                errors.Add("The code is missing or too long.");
            DateOnly date = default;
            if (f.Count > 1 && !DateOnly.TryParseExact(f[1].Trim(), ["yyyy-MM-dd", "d/MM/yyyy", "d/M/yyyy", "dd/MM/yyyy", "d-MMM-yyyy", "d MMM yyyy"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                errors.Add($"'{f[1]}' isn't a date (use e.g. 2026-06-30 or 30/06/2026).");
            else if (date > today)
                errors.Add("The date is in the future.");
            decimal price = 0;
            if (f.Count > 2 && (!BrokerCsv.TryAmount(f[2], out price) || price <= 0))
                errors.Add("The price must be a number above zero.");
            lines.Add(new ParsedLine<ManualPriceRow>(number, line, errors.Count == 0 ? new ManualPriceRow(code, date, price) : null, errors, []));
        }
        return lines.Count == 0 ? ImportPreview<ManualPriceRow>.Failed("The file has no prices in it.") : new(lines);
    }
}
