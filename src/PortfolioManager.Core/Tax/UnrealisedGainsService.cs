using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Tax;

/// <summary>One open parcel, or one holding when grouped. MarketPrice is null when no price is on file.</summary>
public sealed record UnrealisedGainLine(
    string ExchangeCode,
    string StockCode,
    decimal Units,
    decimal CostPerUnit,
    decimal TotalCost,
    decimal? MarketPrice,
    decimal MarketValue,
    decimal Gain);

public sealed record UnrealisedGainsTotal(decimal TotalCost, decimal MarketValue, decimal Gain);

public sealed record UnrealisedGainsReport(IReadOnlyList<UnrealisedGainLine> Lines, UnrealisedGainsTotal Total)
{
    public bool AnyMissingPrices => Lines.Any(l => l.MarketPrice is null);
}

/// <summary>Gains and losses on open parcels if sold at the as-at date's prices (GetUnrealizedCapitalGains).</summary>
public sealed class UnrealisedGainsService(IDbConnectionFactory db)
{
    public async Task<UnrealisedGainsReport> GetAsync(int portfolioId, DateOnly asAt, bool groupByStock)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync("GetUnrealizedCapitalGains",
            new
            {
                iUserID = portfolioId,
                strGroupByStockCode = groupByStock ? "Yes" : "No",
                dtmDetailsAsAtDate = asAt.ToDateTime(TimeOnly.MinValue),
            },
            commandType: CommandType.StoredProcedure);
        return Map(rows.Cast<IDictionary<string, object?>>());
    }

    /// <summary>
    /// Turns the proc's rows into lines plus a total. As with the CGT proc, the "Total:" row is
    /// added with a UNION and can come back anywhere; lines are sorted by code then exchange.
    /// The proc leaves the price NULL (or 0 when grouped) and the value 0 when it has no price,
    /// so a zero price is shown as missing.
    /// </summary>
    public static UnrealisedGainsReport Map(IEnumerable<IDictionary<string, object?>> rows)
    {
        var lines = new List<UnrealisedGainLine>();
        UnrealisedGainsTotal? total = null;

        foreach (var r in rows)
        {
            var exchange = Convert.ToString(r["Stock Exchange"])?.Trim() ?? "";
            if (exchange == "Total:")
            {
                total = new UnrealisedGainsTotal(Num.Dec(r, "Total Cost Basis"), Num.Dec(r, "Market Value"), Num.Dec(r, "Total Gain / Loss"));
                continue;
            }

            var price = Num.DecOrNull(r, "Market Price");
            lines.Add(new UnrealisedGainLine(
                exchange,
                Convert.ToString(r["Stock Code"])?.Trim() ?? "",
                Num.Dec(r, "Current Holding"),
                Num.Dec(r, "Cost Basis Per Unit"),
                Num.Dec(r, "Total Cost Basis"),
                price is null or 0m ? null : price,
                Num.Dec(r, "Market Value"),
                Num.Dec(r, "Total Gain / Loss")));
        }

        lines = lines
            .OrderBy(l => l.StockCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.ExchangeCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        total ??= new UnrealisedGainsTotal(lines.Sum(l => l.TotalCost), lines.Sum(l => l.MarketValue), lines.Sum(l => l.Gain));
        return new UnrealisedGainsReport(lines, total);
    }
}
