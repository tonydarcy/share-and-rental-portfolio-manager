using System.Data;
using System.Globalization;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Tax;

public sealed record CapitalGainLine(
    string ExchangeCode,
    string StockCode,
    DateOnly? DateSold,
    string DateSoldText,
    decimal CostPerUnit,
    decimal TotalCost,
    decimal ProceedsPerUnit,
    decimal TotalProceeds,
    decimal GainPerUnit,
    decimal Gain);

public sealed record CapitalGainsTotal(decimal TotalCost, decimal TotalProceeds, decimal Gain);

public sealed record CapitalGainsReport(IReadOnlyList<CapitalGainLine> Lines, CapitalGainsTotal Total);

/// <summary>Realised capital gains (FIFO, no CGT discount) from the legacy CGT report (GetCapitalGainsTaxSummary).</summary>
public sealed class CapitalGainsService(IDbConnectionFactory db)
{
    public async Task<CapitalGainsReport> GetAsync(int portfolioId, DateOnly from, DateOnly to)
    {
        await using var con = db.Create();
        // The proc's column names contain spaces, so read rows as dictionaries
        var rows = await con.QueryAsync("GetCapitalGainsTaxSummary",
            new { iUserID = portfolioId, dtmStartDate = from.ToDateTime(TimeOnly.MinValue), dtmEndDate = to.ToDateTime(TimeOnly.MinValue) },
            commandType: CommandType.StoredProcedure);
        return Map(rows.Cast<IDictionary<string, object?>>());
    }

    /// <summary>
    /// Turns the proc's rows into sales plus a total. The proc adds its "Total:" row with a UNION,
    /// so it can come back anywhere and the sales come back in no useful order; they're sorted by
    /// date sold then holding. With no sales the total row is all NULLs, so it shows as zero.
    /// </summary>
    public static CapitalGainsReport Map(IEnumerable<IDictionary<string, object?>> rows)
    {
        var lines = new List<CapitalGainLine>();
        CapitalGainsTotal? total = null;

        foreach (var r in rows)
        {
            var item = Convert.ToString(r["Stock Exchange & Stock Code"])?.Trim() ?? "";
            if (item == "Total:")
            {
                total = new CapitalGainsTotal(Num.Dec(r, "Total Cost Basis"), Num.Dec(r, "Total Proceeds"), Num.Dec(r, "Total Gain / Loss"));
                continue;
            }

            // item is "exchange:code"
            var colon = item.IndexOf(':');
            var dateText = r["Date Sold"] is null or DBNull ? "" : Convert.ToString(r["Date Sold"])!.Trim();
            lines.Add(new CapitalGainLine(
                colon >= 0 ? item[..colon] : "",
                colon >= 0 ? item[(colon + 1)..] : item,
                ParseDateSold(dateText),
                dateText,
                Num.Dec(r, "Cost Basis Per Unit"),
                Num.Dec(r, "Total Cost Basis"),
                Num.Dec(r, "Proceeds Per Unit"),
                Num.Dec(r, "Total Proceeds"),
                Num.Dec(r, "Gain / Loss Per Unit"),
                Num.Dec(r, "Total Gain / Loss")));
        }

        lines = lines
            .OrderBy(l => l.DateSold ?? DateOnly.MaxValue)
            .ThenBy(l => l.StockCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.ExchangeCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        total ??= new CapitalGainsTotal(lines.Sum(l => l.TotalCost), lines.Sum(l => l.TotalProceeds), lines.Sum(l => l.Gain));
        return new CapitalGainsReport(lines, total);
    }

    /// <summary>The proc formats the date with CONVERT style 106, e.g. "05 Mar 2025".</summary>
    public static DateOnly? ParseDateSold(string text) =>
        DateOnly.TryParseExact(text, ["dd MMM yyyy", "d MMM yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
