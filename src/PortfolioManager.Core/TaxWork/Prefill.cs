using System.Data;
using System.Globalization;
using System.Xml.Linq;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Imports;

namespace PortfolioManager.Core.TaxWork;

public static class PrefillCategories
{
    public const string Interest = "Interest", InterestTfn = "InterestTfn", Franked = "Franked", Unfranked = "Unfranked",
        FrankingCredit = "FrankingCredit", DividendTfn = "DividendTfn", Dividend = "Dividend", Trust = "Trust", Ess = "Ess",
        ShareSale = "ShareSale", Other = "Other";

    public static readonly IReadOnlyList<string> All = [Interest, InterestTfn, Dividend, Franked, Unfranked, FrankingCredit, DividendTfn, Trust, Ess, ShareSale, Other];

    public static string Label(string c) => c switch
    {
        Interest => "Gross interest",
        InterestTfn => "TFN withheld from interest",
        Dividend => "Dividends (not split)",
        Franked => "Franked dividends",
        Unfranked => "Unfranked dividends",
        FrankingCredit => "Franking credits",
        DividendTfn => "TFN withheld from dividends",
        Trust => "Managed fund and trust distributions",
        Ess => "Employee share scheme discounts",
        ShareSale => "Share sales (ATO data matching)",
        _ => "Other",
    };
}

public sealed record PrefillItem(string Category, string? Payer, decimal Amount);

/// <summary>A prefill figure against what the ledger and payouts have for the year.</summary>
public sealed record PrefillComparison(string Category, decimal Prefill, decimal? Ours, string Where)
{
    public decimal? Difference => Ours is { } o ? o - Prefill : null;

    /// <summary>Within a dollar either way counts as agreeing (the ATO rounds some figures to whole dollars).</summary>
    public bool Agrees => Difference is { } d && Math.Abs(d) < 1m;
}

/// <summary>
/// Reads the prefill figures from a CSV or pasted table: a heading row, then one row per payer. Columns are found by their
/// headings (type, payer, amount, franked, unfranked, franking credit, tax or TFN withheld), so the report can be copied from
/// Online services for agents or typed in.
/// </summary>
public static class PrefillParser
{
    public static (IReadOnlyList<PrefillItem> Items, IReadOnlyList<string> Problems) Parse(string text)
    {
        var lines = CsvImport.Lines(text).ToList();
        if (lines.Count < 2)
            return ([], ["Paste a heading row and at least one row of figures."]);
        var tab = lines[0].Text.Contains('\t');
        IReadOnlyList<string> Fields(string line) => tab ? line.Split('\t').Select(f => f.Trim()).ToList() : CsvImport.Split(line);

        var heads = Fields(lines[0].Text).Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(Func<string, bool> match) => heads.FindIndex(h => match(h));
        var type = Col(h => h.Contains("type") || h.Contains("category") || h.Contains("description") || h.Contains("income"));
        var payer = Col(h => h.Contains("payer") || h.Contains("name") || h.Contains("source") || h.Contains("institution") || h.Contains("company"));
        var unfranked = Col(h => h.Contains("unfranked"));
        var franked = Col(h => h.Contains("franked") && !h.Contains("unfranked"));
        var credit = Col(h => h.Contains("credit"));
        var withheld = Col(h => h.Contains("withheld") || h.Contains("tfn") || (h.Contains("tax") && !h.Contains("credit")));
        var amount = Col(h => (h.Contains("amount") || h.Contains("gross") || h.Contains("interest") || h.Contains("value") || h.Contains("proceeds"))
            && !h.Contains("franked") && !h.Contains("withheld") && !h.Contains("credit"));
        if (amount < 0 && franked < 0 && unfranked < 0)
            return ([], ["No amount column was found. Name one of the headings Amount, Franked or Unfranked."]);

        var items = new List<PrefillItem>();
        var problems = new List<string>();
        foreach (var (number, line) in lines.Skip(1))
        {
            var f = Fields(line);
            string Get(int i) => i >= 0 && i < f.Count ? f[i].Trim() : "";
            decimal? Num(int i)
            {
                var s = Get(i).Replace("$", "").Replace(",", "").Trim();
                if (s.StartsWith('(') && s.EndsWith(')'))
                    s = "-" + s[1..^1];
                return s.Length == 0 ? null : decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : decimal.MinValue;
            }
            var values = new[] { amount, franked, unfranked, credit, withheld }.Where(i => i >= 0).Select(Num).ToList();
            if (values.Any(v => v == decimal.MinValue))
            {
                problems.Add($"Line {number}: an amount isn't a number, so the line was skipped.");
                continue;
            }
            var kind = Kind(Get(type), franked >= 0 && (Num(franked) ?? 0) != 0 || unfranked >= 0 && (Num(unfranked) ?? 0) != 0);
            var who = Get(payer) is { Length: > 0 } p ? p : null;
            void Add(string category, decimal? v)
            {
                if (v is { } x && x != 0)
                    items.Add(new PrefillItem(category, who, x));
            }
            switch (kind)
            {
                case PrefillCategories.Interest:
                    Add(PrefillCategories.Interest, Num(amount));
                    Add(PrefillCategories.InterestTfn, Num(withheld));
                    break;
                case PrefillCategories.Dividend:
                    Add(PrefillCategories.Franked, Num(franked));
                    Add(PrefillCategories.Unfranked, Num(unfranked));
                    Add(PrefillCategories.FrankingCredit, Num(credit));
                    Add(PrefillCategories.DividendTfn, Num(withheld));
                    if (franked < 0 && unfranked < 0)
                        Add(PrefillCategories.Dividend, Num(amount));
                    break;
                default:
                    Add(kind, Num(amount));
                    break;
            }
        }
        if (items.Count == 0 && problems.Count == 0)
            problems.Add("No figures were found.");
        return (items, problems);
    }

    public static string Kind(string text, bool hasFranking)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("interest")) return PrefillCategories.Interest;
        if (t.Contains("dividend") || hasFranking) return PrefillCategories.Dividend;
        if (t.Contains("trust") || t.Contains("distribution") || t.Contains("managed")) return PrefillCategories.Trust;
        if (t.Contains("employee") || t.Split(' ', '-').Contains("ess")) return PrefillCategories.Ess;
        if (t.Contains("share") || t.Contains("sale") || t.Contains("disposal")) return PrefillCategories.ShareSale;
        return PrefillCategories.Other;
    }

    public static string ToXml(IEnumerable<PrefillItem> items) =>
        string.Concat(items.Select(i => new XElement("i", new XAttribute("c", i.Category), new XAttribute("p", i.Payer ?? ""),
            new XAttribute("v", i.Amount.ToString(CultureInfo.InvariantCulture))).ToString(SaveOptions.DisableFormatting)));

    /// <summary>The prefill totals by category set against the year's payouts, fund statements and ESS statements.</summary>
    public static IReadOnlyList<PrefillComparison> Compare(IEnumerable<PrefillItem> items, TaxWorkpaper w)
    {
        var i = w.Income;
        var totals = items.GroupBy(x => x.Category).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        (decimal? Ours, string Where) Ours(string c) => c switch
        {
            PrefillCategories.Interest => (i.Interest, "Payouts: interest"),
            PrefillCategories.InterestTfn => (i.TfnOnInterest, "Payouts: tax withheld on interest"),
            PrefillCategories.Franked => (i.Franked, "Payouts: franked"),
            PrefillCategories.Unfranked => (i.Unfranked, "Payouts: unfranked"),
            PrefillCategories.FrankingCredit => (i.DividendFrankingCredits, "Payouts: franking credits on dividends"),
            PrefillCategories.DividendTfn => (i.TfnOnDividends, "Payouts: tax withheld on dividends"),
            PrefillCategories.Dividend => (i.Franked + i.Unfranked, "Payouts: franked plus unfranked"),
            PrefillCategories.Trust => (i.TrustIncome + i.TrustFranked + i.ForeignIncome, "Payouts and fund tax statements, before capital gains"),
            PrefillCategories.Ess => (w.Ess.TotalDiscount, "ESS statements"),
            _ => (null, c == PrefillCategories.ShareSale ? "Check each sale against Reports > Capital gains" : "Check by hand"),
        };
        return PrefillCategories.All.Where(totals.ContainsKey)
            .Select(c => { var (ours, where) = Ours(c); return new PrefillComparison(c, totals[c], ours, where); })
            .ToList();
    }
}

/// <summary>The imported prefill figures (2026-10-21-21-ato-prefill.sql).</summary>
public sealed class PrefillService(IDbConnectionFactory db)
{
    public async Task<(IReadOnlyList<PrefillItem> Items, DateTime? Imported)> GetAsync(int portfolioId, int year)
    {
        await using var con = db.Create();
        try
        {
            var rows = (await con.QueryAsync(
                    "SELECT strCategory, strPayer, dblAmount, dtmImported FROM tblPrefillItems WHERE iUserID = @portfolioId AND iYear = @year ORDER BY iItemID",
                    new { portfolioId, year }))
                .Cast<IDictionary<string, object?>>().ToList();
            return (rows.Select(r => new PrefillItem(Db.Str(r, "strCategory"), Db.Str(r, "strPayer") is { Length: > 0 } p ? p : null, Db.Dec(r, "dblAmount") ?? 0)).ToList(),
                rows.Count == 0 ? null : rows[0]["dtmImported"] as DateTime?);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return ([], null);
        }
    }

    public async Task<ProcResult> ReplaceAsync(int portfolioId, int year, IEnumerable<PrefillItem> items)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspReplacePrefillItems",
            new { iUserID = portfolioId, iYear = year, xmlItems = PrefillParser.ToXml(items) }, commandType: CommandType.StoredProcedure), "Success");
    }
}
