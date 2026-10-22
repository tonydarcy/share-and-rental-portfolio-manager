using System.Globalization;
using System.Text;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.SalesTax;

/// <summary>
/// One journal line with a tax code (uspGetSalesTaxLines). Net and Tax are signed the way the
/// report adds them: a sale or a purchase is positive, a refund of either is negative.
/// </summary>
public sealed record SalesTaxLine(
    int JournalId,
    DateOnly Date,
    string Description,
    string Source,
    string AccountName,
    int TaxCodeId,
    decimal Net,
    decimal Tax)
{
    public decimal Gross => Net + Tax;
}

/// <summary>The totals for one tax code over the period.</summary>
public sealed record TaxCodeTotal(TaxCode Code, int Count, decimal Net, decimal Tax)
{
    public decimal Gross => Net + Tax;
}

/// <summary>A box of the tax return (BAS label, VAT return box) and what goes in it.</summary>
public sealed record ReturnBox(string Box, decimal Amount, IReadOnlyList<string> Codes);

public sealed record SalesTaxReport(
    string TaxName,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<TaxCodeTotal> Codes,
    IReadOnlyList<ReturnBox> Boxes,
    IReadOnlyList<SalesTaxLine> Lines,
    IReadOnlyDictionary<int, TaxCode> CodesById)
{
    public decimal TaxOnSales => Codes.Where(c => c.Code.IsSales).Sum(c => c.Tax);
    public decimal TaxOnPurchases => Codes.Where(c => !c.Code.IsSales).Sum(c => c.Tax);

    /// <summary>Positive: owed to the tax office. Negative: a refund is due.</summary>
    public decimal NetTax => TaxOnSales - TaxOnPurchases;

    public decimal TotalSales => Codes.Where(c => c.Code.IsSales).Sum(c => c.Net);
    public decimal TotalPurchases => Codes.Where(c => !c.Code.IsSales).Sum(c => c.Net);

    /// <summary>
    /// Adds up the lines by tax code and by return box. A code's amount goes into each of its
    /// amount boxes (with or without tax, as the code says), and its tax into its tax box.
    /// </summary>
    public static SalesTaxReport Build(string taxName, DateOnly from, DateOnly to, IReadOnlyList<TaxCode> codes, IReadOnlyList<SalesTaxLine> lines)
    {
        var byId = codes.ToDictionary(c => c.TaxCodeId);
        var used = lines.Where(l => byId.ContainsKey(l.TaxCodeId)).ToList();

        var totals = used.GroupBy(l => l.TaxCodeId)
            .Select(g => new TaxCodeTotal(byId[g.Key], g.Count(), g.Sum(l => l.Net), g.Sum(l => l.Tax)))
            .OrderBy(t => t.Code.IsSales ? 0 : 1).ThenBy(t => t.Code.SortOrder).ThenBy(t => t.Code.Code)
            .ToList();

        var boxes = new Dictionary<string, (decimal Amount, List<string> Codes)>(StringComparer.OrdinalIgnoreCase);
        void Add(string box, decimal amount, string code)
        {
            var entry = boxes.TryGetValue(box, out var e) ? e : (0m, new List<string>());
            if (!entry.Item2.Contains(code))
                entry.Item2.Add(code);
            boxes[box] = (entry.Item1 + amount, entry.Item2);
        }

        // Every box a code names shows, even with nothing in it this period, so the return reads complete
        foreach (var code in codes.Where(c => c.IsActive || used.Any(l => l.TaxCodeId == c.TaxCodeId)))
        {
            var total = totals.FirstOrDefault(t => t.Code.TaxCodeId == code.TaxCodeId);
            foreach (var box in code.AmountBoxList)
                Add(box, total is null ? 0 : code.AmountIncludesTax ? total.Gross : total.Net, code.Code);
            if (code.TaxBox is { Length: > 0 } taxBox)
                Add(taxBox.Trim(), total?.Tax ?? 0, code.Code);
        }

        var boxList = boxes.Select(b => new ReturnBox(b.Key, b.Value.Amount, b.Value.Codes))
            .OrderBy(b => b.Box, BoxComparer.Instance)
            .ToList();

        return new SalesTaxReport(taxName, from, to, totals, boxList, used, byId);
    }

    /// <summary>The report as a CSV workpaper: the summary, the boxes, the codes, then every transaction.</summary>
    public byte[] ToCsv()
    {
        static string N(decimal d) => d.ToString("0.00", CultureInfo.InvariantCulture);
        var c = TaxWork.LabelSummaryCsv.Csv;
        var sb = new StringBuilder();
        sb.AppendLine(c($"{TaxName} report {From:yyyy-MM-dd} to {To:yyyy-MM-dd}"));
        sb.AppendLine();
        sb.AppendLine("Summary,Amount");
        sb.AppendLine($"{c($"{TaxName} on sales")},{N(TaxOnSales)}");
        sb.AppendLine($"{c($"{TaxName} on purchases")},{N(TaxOnPurchases)}");
        sb.AppendLine($"{c(NetTax >= 0 ? "Net amount to pay" : "Net refund due")},{N(Math.Abs(NetTax))}");
        sb.AppendLine();
        sb.AppendLine("Box,Amount,Tax codes");
        foreach (var b in Boxes)
            sb.AppendLine($"{c(b.Box)},{N(b.Amount)},{c(string.Join(" ", b.Codes))}");
        sb.AppendLine();
        sb.AppendLine("Tax code,Name,Kind,Rate,Transactions,Amount before tax,Tax,Amount with tax");
        foreach (var t in Codes)
            sb.AppendLine(string.Join(',', c(t.Code.Code), c(t.Code.Name), t.Code.Kind,
                t.Code.Rate.ToString("0.####", CultureInfo.InvariantCulture), t.Count, N(t.Net), N(t.Tax), N(t.Gross)));
        sb.AppendLine();
        sb.AppendLine("Date,Journal,Description,Account,Tax code,Amount before tax,Tax,Amount with tax");
        foreach (var l in Lines)
            sb.AppendLine(string.Join(',', l.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), l.JournalId,
                c(l.Description), c(l.AccountName), c(CodesById[l.TaxCodeId].Code), N(l.Net), N(l.Tax), N(l.Gross)));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }
}

/// <summary>Sorts box names the way they're printed on a return: G1, G2, G10, then 1A, 1B; 1, 4, 6, 14.</summary>
public sealed class BoxComparer : IComparer<string>
{
    public static readonly BoxComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var a = Parts(x ?? "");
        var b = Parts(y ?? "");
        var c = a.Rank.CompareTo(b.Rank);
        if (c == 0) c = string.Compare(a.Letters, b.Letters, StringComparison.OrdinalIgnoreCase);
        if (c == 0) c = a.Number.CompareTo(b.Number);
        if (c == 0) c = string.Compare(a.Suffix, b.Suffix, StringComparison.OrdinalIgnoreCase);
        return c;
    }

    /// <summary>
    /// Lettered boxes (G1, T1) come first as on a BAS, then numbered ones (1A, 6, 3.1(a)), then
    /// words ("Gross sales"). "G10" is G, 10, ""; "1A" is "", 1, "A".
    /// </summary>
    private static (int Rank, string Letters, long Number, string Suffix) Parts(string box)
    {
        var s = box.Trim();
        var i = 0;
        while (i < s.Length && char.IsLetter(s[i])) i++;
        var j = i;
        while (j < s.Length && char.IsDigit(s[j])) j++;
        if (j == i)
            return (2, s, 0, "");
        var number = long.TryParse(s[i..j], out var n) ? n : long.MaxValue;
        return (i > 0 ? 0 : 1, s[..i], number, s[j..]);
    }
}

/// <summary>One reporting period (a month, quarter...) of a financial year.</summary>
public sealed record TaxPeriod(DateOnly From, DateOnly To)
{
    public string Key => From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string Label => From.Year == To.Year && From.Month == To.Month
        ? From.ToString("MMMM yyyy", CultureInfo.CurrentCulture)
        : From.Year == To.Year
            ? $"{From.ToString("MMM", CultureInfo.CurrentCulture)} to {To.ToString("MMM yyyy", CultureInfo.CurrentCulture)}"
            : $"{From.ToString("MMM yyyy", CultureInfo.CurrentCulture)} to {To.ToString("MMM yyyy", CultureInfo.CurrentCulture)}";
}

public static class TaxPeriods
{
    /// <summary>
    /// The periods of a financial year, each <paramref name="months"/> long from the first of the
    /// month the year starts in (a UK year starting 6 April has VAT quarters from 1 April).
    /// </summary>
    public static IReadOnlyList<TaxPeriod> ForYear(int endYear, int months, YearStart? start = null)
    {
        months = SalesTaxSetup.PeriodChoices.Contains(months) ? months : 3;
        var yearStart = FinancialYear.StartDate(endYear, start);
        var first = new DateOnly(yearStart.Year, yearStart.Month, 1);
        return Enumerable.Range(0, 12 / months)
            .Select(i => new TaxPeriod(first.AddMonths(i * months), first.AddMonths((i + 1) * months).AddDays(-1)))
            .ToList();
    }

    /// <summary>The periods of this financial year and the last two that have started, newest first.</summary>
    public static IReadOnlyList<TaxPeriod> Recent(DateOnly today, int months, YearStart? start = null)
    {
        var year = FinancialYear.EndYearFor(today, start);
        return Enumerable.Range(0, 3)
            .SelectMany(i => ForYear(year - i, months, start))
            .Where(p => p.From <= today)
            .OrderByDescending(p => p.From)
            .ToList();
    }

    /// <summary>The latest period that has ended, the one usually being lodged.</summary>
    public static TaxPeriod LastCompleted(DateOnly today, int months, YearStart? start = null) =>
        Recent(today, months, start).First(p => p.To < today);

    /// <summary>
    /// The dates the report covers: a period picked from the list wins; otherwise both dates;
    /// otherwise <paramref name="fallback"/>, the last period that has ended.
    /// </summary>
    public static (DateOnly From, DateOnly To, string? Warning) Resolve(string? period, DateOnly? from, DateOnly? to, IReadOnlyList<TaxPeriod> periods, TaxPeriod fallback)
    {
        if (!string.IsNullOrEmpty(period) && periods.FirstOrDefault(p => p.Key == period) is { } picked)
            return (picked.From, picked.To, null);
        if (from is not null && to is not null)
            return from <= to
                ? (from.Value, to.Value, null)
                : (fallback.From, fallback.To, "The From date is after the To date, so the last period is shown instead.");
        return (fallback.From, fallback.To, null);
    }
}
