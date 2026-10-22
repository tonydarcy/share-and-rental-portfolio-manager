using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Reports;

public sealed record DiversityLine(string Sector, string Industry, decimal Value, decimal Share);

public sealed record SectorTotal(string Sector, decimal Value, decimal Share);

public sealed record DiversityReport(IReadOnlyList<DiversityLine> Lines, IReadOnlyList<SectorTotal> Sectors, decimal Total);

/// <summary>Market value of current holdings by sector and industry (GetDiversityReport).</summary>
public sealed class DiversityService(IDbConnectionFactory db)
{
    public async Task<DiversityReport> GetAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync("GetDiversityReport", new { iUserId = portfolioId },
            commandType: CommandType.StoredProcedure);
        return Map(rows.Cast<IDictionary<string, object?>>());
    }

    /// <summary>
    /// Splits the proc's rows into lines and its total (RowOrder 2). Shares are worked out here
    /// as fractions rather than read from the proc's formatted Percentage text.
    /// </summary>
    public static DiversityReport Map(IEnumerable<IDictionary<string, object?>> rows)
    {
        var raw = new List<(string Sector, string Industry, decimal Value)>();
        decimal? total = null;

        foreach (var r in rows)
        {
            var value = r["Value"] is null or DBNull ? 0m : Convert.ToDecimal(r["Value"]);
            if (Convert.ToInt32(r["RowOrder"]) == 2)
            {
                total = value;
                continue;
            }
            raw.Add((Text(r["Sector"]), Text(r["Industry"]), value));
        }

        var sum = total is > 0 ? total.Value : raw.Sum(l => l.Value);
        decimal Share(decimal v) => sum == 0 ? 0 : v / sum;

        var lines = raw
            .OrderByDescending(l => l.Value)
            .Select(l => new DiversityLine(l.Sector, l.Industry, l.Value, Share(l.Value)))
            .ToList();
        var sectors = raw
            .GroupBy(l => l.Sector)
            .Select(g => new SectorTotal(g.Key, g.Sum(l => l.Value), Share(g.Sum(l => l.Value))))
            .OrderByDescending(s => s.Value)
            .ToList();
        return new DiversityReport(lines, sectors, sum);
    }

    private static string Text(object? value) =>
        value is null or DBNull || Convert.ToString(value)!.Trim() is not { Length: > 0 } s ? "Unknown" : s;
}
