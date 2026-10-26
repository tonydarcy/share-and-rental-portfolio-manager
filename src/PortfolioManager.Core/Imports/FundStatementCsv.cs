using System.Globalization;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.Imports;

/// <summary>
/// Annual fund tax statements (AMIT member annual statements, or standard distribution statements) from a file: one line per
/// fund per year. Registries and fund managers name their columns differently, so each column is found by its header
/// (for example "Franked distributions", "AMIT cost base net amount - excess" or "Foreign income tax offset"). Code and
/// Year are needed, plus any of the amounts; a missing amount is nil. Exchange is ASX when there's no exchange column.
/// </summary>
public static class FundStatementCsv
{
    public const string Template = "Exchange,Code,Year,Cash distribution,Franked,Franking credits,Unfranked,Interest,Other income,NCMI," +
        "Discounted gain,Other gain,Foreign income,Foreign tax credits,TFN withheld,Cost base increase,Cost base decrease,Comment";

    private enum Col { Exchange, Code, Year, Cash, Franked, FrankingCredits, Unfranked, Interest, Other, Ncmi, DiscountedGain, OtherGain,
        ForeignIncome, ForeignTax, Tfn, Increase, Decrease, Comment }

    // Header names with everything but letters and digits taken out, lower case
    private static readonly Dictionary<string, Col> Names = new()
    {
        ["exchange"] = Col.Exchange, ["market"] = Col.Exchange, ["stockexchange"] = Col.Exchange,
        ["code"] = Col.Code, ["asxcode"] = Col.Code, ["fundcode"] = Col.Code, ["ticker"] = Col.Code, ["securitycode"] = Col.Code,
        ["stockcode"] = Col.Code, ["issuercode"] = Col.Code,
        ["year"] = Col.Year, ["financialyear"] = Col.Year, ["taxyear"] = Col.Year, ["incomeyear"] = Col.Year, ["fy"] = Col.Year,
        ["yearending"] = Col.Year, ["yearend"] = Col.Year, ["periodending"] = Col.Year, ["periodend"] = Col.Year,
        ["cashdistribution"] = Col.Cash, ["cashdistributions"] = Col.Cash, ["netcashdistribution"] = Col.Cash, ["netcashdistributions"] = Col.Cash,
        ["totalcashdistribution"] = Col.Cash, ["totalcashdistributions"] = Col.Cash, ["cashpaid"] = Col.Cash, ["netcashpaid"] = Col.Cash,
        ["franked"] = Col.Franked, ["frankeddistribution"] = Col.Franked, ["frankeddistributions"] = Col.Franked, ["frankedamount"] = Col.Franked,
        ["frankeddistributionsfromtrusts"] = Col.Franked, ["frankeddividends"] = Col.Franked,
        ["frankingcredit"] = Col.FrankingCredits, ["frankingcredits"] = Col.FrankingCredits, ["frankingcredittaxoffset"] = Col.FrankingCredits,
        ["shareoffrankingcredits"] = Col.FrankingCredits, ["shareoffrankingcredit"] = Col.FrankingCredits,
        ["unfranked"] = Col.Unfranked, ["unfrankeddistribution"] = Col.Unfranked, ["unfrankeddistributions"] = Col.Unfranked,
        ["unfrankedamount"] = Col.Unfranked, ["unfrankeddividends"] = Col.Unfranked,
        ["interest"] = Col.Interest, ["interestincome"] = Col.Interest, ["australianinterest"] = Col.Interest,
        ["otherincome"] = Col.Other, ["otherassessableincome"] = Col.Other, ["otheraustralianincome"] = Col.Other,
        ["ncmi"] = Col.Ncmi, ["nonconcessionalmitincome"] = Col.Ncmi, ["nonconcessionalmanagedinvestmenttrustincome"] = Col.Ncmi,
        ["discountedgain"] = Col.DiscountedGain, ["discountedcapitalgain"] = Col.DiscountedGain, ["discountedcapitalgains"] = Col.DiscountedGain,
        ["capitalgainsdiscounted"] = Col.DiscountedGain, ["discountcapitalgain"] = Col.DiscountedGain, ["discountcapitalgains"] = Col.DiscountedGain,
        ["capitalgainsdiscountmethod"] = Col.DiscountedGain,
        ["othergain"] = Col.OtherGain, ["othercapitalgain"] = Col.OtherGain, ["othercapitalgains"] = Col.OtherGain,
        ["capitalgainsothermethod"] = Col.OtherGain, ["nondiscountedcapitalgain"] = Col.OtherGain, ["nondiscountcapitalgains"] = Col.OtherGain,
        ["foreignincome"] = Col.ForeignIncome, ["assessableforeignincome"] = Col.ForeignIncome, ["foreignsourceincome"] = Col.ForeignIncome,
        ["otherassessableforeignsourceincome"] = Col.ForeignIncome,
        ["foreigntaxcredits"] = Col.ForeignTax, ["foreigntaxcredit"] = Col.ForeignTax, ["foreignincometaxoffset"] = Col.ForeignTax,
        ["foreignincometaxoffsets"] = Col.ForeignTax, ["foreigntaxoffset"] = Col.ForeignTax, ["fito"] = Col.ForeignTax, ["foreigntaxpaid"] = Col.ForeignTax,
        ["tfnwithheld"] = Col.Tfn, ["tfnamountswithheld"] = Col.Tfn, ["tfnamountwithheld"] = Col.Tfn, ["tfnwithholding"] = Col.Tfn,
        ["costbaseincrease"] = Col.Increase, ["amitcostbaseincrease"] = Col.Increase, ["amitcostbasenetamountshortfall"] = Col.Increase,
        ["amitcostbasenetincrease"] = Col.Increase, ["amitshortfall"] = Col.Increase, ["costbasenetincrease"] = Col.Increase,
        ["costbasedecrease"] = Col.Decrease, ["amitcostbasedecrease"] = Col.Decrease, ["amitcostbasenetamountexcess"] = Col.Decrease,
        ["amitcostbasenetdecrease"] = Col.Decrease, ["amitexcess"] = Col.Decrease, ["costbasenetdecrease"] = Col.Decrease,
        ["taxdeferred"] = Col.Decrease, ["taxdeferredamount"] = Col.Decrease, ["taxdeferredamounts"] = Col.Decrease,
        ["comment"] = Col.Comment, ["comments"] = Col.Comment, ["notes"] = Col.Comment, ["fundname"] = Col.Comment,
    };

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "d/MM/yyyy", "dd/MM/yyyy", "d/M/yyyy", "d-MMM-yyyy", "dd-MMM-yyyy", "d MMM yyyy", "dd MMM yyyy"];

    public static ImportPreview<FundTaxStatement> Parse(string text, YearStart? start = null)
    {
        var lines = CsvImport.Lines(text).ToList();
        if (lines.Count > CsvImport.MaxLines)
            return ImportPreview<FundTaxStatement>.Failed($"The file has more than {CsvImport.MaxLines:N0} lines; split it into smaller files.");

        // The header can come after lines about the registry or investor
        Dictionary<Col, int>? columns = null;
        var headerLine = -1;
        for (var i = 0; i < Math.Min(lines.Count, 20) && columns is null; i++)
        {
            var found = Header(CsvImport.Split(lines[i].Text));
            if (found.ContainsKey(Col.Code) && found.ContainsKey(Col.Year) && found.Keys.Any(k => k >= Col.Cash && k <= Col.Decrease))
                (columns, headerLine) = (found, i);
        }
        if (columns is null)
            return ImportPreview<FundTaxStatement>.Failed("Couldn't find the header row. The file needs Code and Year columns and at least one amount, for example: " + Template);

        var parsed = new List<ParsedLine<FundTaxStatement>>();
        var seen = new HashSet<(string, string, int)>();
        foreach (var (number, line) in lines.Skip(headerLine + 1))
        {
            var f = CsvImport.Split(line);
            string Get(Col c) => columns.TryGetValue(c, out var i) && i < f.Count ? f[i].Trim() : "";
            var errors = new List<string>();
            var code = Get(Col.Code).ToUpperInvariant();
            if (code.Length == 0 && f.All(x => x.Length == 0 || !char.IsDigit(x.FirstOrDefault())))
                continue; // a note or blank line, not a statement
            var exchange = Get(Col.Exchange) is { Length: > 0 } e ? e.ToUpperInvariant() : "ASX";
            if (code.Length is 0 or > 50)
                errors.Add("The fund code is missing or too long.");
            var year = Year(Get(Col.Year), start);
            if (year is null)
                errors.Add($"'{Get(Col.Year)}' isn't a financial year (use e.g. 2026, 2025-26 or 30/06/2026).");
            decimal Amount(Col c)
            {
                var s = Get(c).Replace("$", "").Trim();
                if (s.Length == 0 || s == "-")
                    return 0;
                var negative = s.StartsWith('(') && s.EndsWith(')');
                if (negative)
                    s = s[1..^1];
                if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
                {
                    errors.Add($"'{Get(c)}' isn't an amount.");
                    return 0;
                }
                return Math.Round(negative ? -v : v, 2, MidpointRounding.AwayFromZero);
            }
            var increase = Amount(Col.Increase);
            var decrease = Amount(Col.Decrease);
            // Some files give one net AMIT column, negative for an increase
            if (decrease < 0 && increase == 0)
                (increase, decrease) = (-decrease, 0);
            if (increase > 0 && decrease > 0)
                (increase, decrease) = (Math.Max(0, increase - decrease), Math.Max(0, decrease - increase));
            FundTaxStatement? statement = null;
            if (errors.Count == 0)
            {
                var y = year!.Value;
                statement = new FundTaxStatement(0, exchange, code, y, FinancialYear.EndDate(y, start), Amount(Col.Cash), Amount(Col.Franked),
                    Amount(Col.FrankingCredits), Amount(Col.Unfranked), Amount(Col.Interest), Amount(Col.Other), Amount(Col.Ncmi),
                    Amount(Col.DiscountedGain), Amount(Col.OtherGain), Amount(Col.ForeignIncome), Amount(Col.ForeignTax), Amount(Col.Tfn),
                    increase, decrease, Get(Col.Comment) is { Length: > 0 } c ? c[..Math.Min(c.Length, 500)] : "Imported from a statement file");
                errors.AddRange(FundTaxStatements.Validate(statement));
                if (errors.Count == 0 && !seen.Add((exchange, code, y)))
                    errors.Add($"{code} for {FinancialYear.Label(y, start)} appears more than once in the file.");
            }
            var warnings = statement is { } s && errors.Count == 0 && Math.Abs(s.Difference) >= 1
                ? new[] { $"The parts are {Math.Abs(s.Difference):N2} {(s.Difference > 0 ? "more" : "less")} than the cash distribution." }
                : [];
            parsed.Add(new ParsedLine<FundTaxStatement>(number, line, errors.Count == 0 ? statement : null, errors, warnings));
        }
        return parsed.Count == 0 ? ImportPreview<FundTaxStatement>.Failed("The file has no statements under its header row.") : new(parsed);
    }

    private static Dictionary<Col, int> Header(IReadOnlyList<string> fields)
    {
        var found = new Dictionary<Col, int>();
        for (var i = 0; i < fields.Count; i++)
        {
            var key = new string(fields[i].Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (Names.TryGetValue(key, out var col))
                found.TryAdd(col, i);
        }
        return found;
    }

    /// <summary>The financial year a statement is for, as the year it ends: "2026", "2025-26", "2025/2026", "FY2026", "FY26" or the year-end date.</summary>
    public static int? Year(string s, YearStart? start = null)
    {
        var t = s.Trim().ToUpperInvariant();
        if (t.StartsWith("FY"))
            t = t[2..].Trim();
        if (DateOnly.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return FinancialYear.EndYearFor(d, start);
        var parts = t.Split('-', '/');
        if (parts is [var a, var b] && int.TryParse(a, out var first) && int.TryParse(b, out var second) && first >= 1900)
        {
            var end = b.Length == 2 ? first / 100 * 100 + second + (second < first % 100 ? 100 : 0) : second;
            return end == first + 1 ? end : null;
        }
        if (int.TryParse(t, out var y))
            return t.Length == 2 ? 2000 + y : y is >= 2000 and <= 2100 ? y : null;
        return null;
    }
}
