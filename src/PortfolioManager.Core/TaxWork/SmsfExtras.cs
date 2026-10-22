using System.Data;
using System.Globalization;
using System.Xml.Linq;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.TaxWork;

/// <summary>Transfer balance account report events: credits add to a member's transfer balance account, debits take it down.</summary>
public static class TbarEventTypes
{
    public const string Commenced = "Commenced", Commutation = "Commutation", StopsRetirement = "StopsRetirement", Lrba = "Lrba",
        StructuredSettlement = "StructuredSettlement", OtherCredit = "OtherCredit", OtherDebit = "OtherDebit";

    public static readonly IReadOnlyList<string> All = [Commenced, Commutation, StopsRetirement, Lrba, StructuredSettlement, OtherCredit, OtherDebit];

    public static bool IsCredit(string type) => type is Commenced or Lrba or StructuredSettlement or OtherCredit;

    public static string Label(string type) => type switch
    {
        Commenced => "Retirement phase income stream started (value at the start)",
        Commutation => "Commutation (lump sum taken out of a pension)",
        StopsRetirement => "Income stream stopped being in retirement phase",
        Lrba => "Limited recourse borrowing repayment",
        StructuredSettlement => "Structured settlement contribution",
        OtherCredit => "Other credit",
        OtherDebit => "Other debit",
        _ => type,
    };
}

public sealed record TbarEvent(int Id, int MemberId, DateOnly Date, string Type, decimal Amount, string? Note, DateOnly? Reported)
{
    public bool IsCredit => TbarEventTypes.IsCredit(Type);
    public decimal Signed => IsCredit ? Amount : -Amount;

    /// <summary>Every SMSF reports quarterly: 28 days after the end of the quarter the event happened in.</summary>
    public DateOnly Due => Tbar.DueDate(Date);
}

public static class Tbar
{
    public static DateOnly DueDate(DateOnly eventDate)
    {
        var quarterEndMonth = (eventDate.Month - 1) / 3 * 3 + 3;
        var quarterEnd = new DateOnly(eventDate.Year, quarterEndMonth, 1).AddMonths(1).AddDays(-1);
        return quarterEnd.AddDays(28);
    }

    /// <summary>Each member's transfer balance account at the date: the credits less the debits up to then.</summary>
    public static IReadOnlyDictionary<int, decimal> Balances(IEnumerable<TbarEvent> events, DateOnly asAt) =>
        events.Where(e => e.Date <= asAt).GroupBy(e => e.MemberId).ToDictionary(g => g.Key, g => g.Sum(e => e.Signed));
}

public static class EcpiMethods
{
    public const string Segregated = "Segregated", Proportionate = "Proportionate", Both = "Both";
    public static readonly IReadOnlyList<string> All = [Segregated, Proportionate, Both];

    /// <summary>
    /// What the year's accounts suggest: no pension, no ECPI; every account in retirement phase all year, segregated with no
    /// actuary's certificate; a mix, proportionate with a certificate.
    /// </summary>
    public static (string? Method, bool NeedsCertificate, string Reason) Suggest(SmsfYearData data, int year)
    {
        var accounts = data.Accounts.Where(a => a.Year == year && (a.Opening != 0 || a.Inflows != 0 || a.Outflows != 0)).ToList();
        if (!accounts.Any(a => a.IsPension))
            return (null, false, "No member account is in retirement phase this year, so there's no exempt pension income.");
        if (accounts.All(a => a.IsPension))
            return (Segregated, false, "Every member account is in retirement phase, so all the fund's income is exempt (segregated method). No actuary's certificate is needed if that was so for the whole year.");
        return (Proportionate, true, "Some accounts are in retirement phase and some in accumulation, so the proportionate method applies and an actuary's certificate is needed.");
    }
}

/// <summary>The trustees' investment strategy for a year (SIS regulation 4.09): target ranges and the matters they considered.</summary>
public sealed class InvestmentStrategy
{
    public static readonly IReadOnlyList<(string Key, string Name)> Classes =
    [
        ("AusShares", "Australian shares"), ("IntShares", "International shares"), ("Property", "Property"), ("Fixed", "Fixed interest"),
        ("Cash", "Cash"), ("Other", "Other (collectables, crypto, unlisted)"),
    ];

    public static readonly IReadOnlyList<(string Key, string Name, string Hint)> Matters =
    [
        ("Objectives", "Objectives", "The return the fund aims for, for example CPI plus 3% a year over rolling 5 years"),
        ("Risk", "Risk and return", "The risk of the investments and the likely return, given the members' ages and goals"),
        ("Diversification", "Diversification", "How the investments are spread, and the risk of not spreading them"),
        ("Liquidity", "Liquidity", "How easily investments can be sold to pay benefits, pensions and costs"),
        ("Liabilities", "Paying liabilities", "Whether the fund can meet its current and expected liabilities"),
        ("Insurance", "Insurance", "Whether the trustees should hold insurance cover for the members"),
    ];

    public Dictionary<string, decimal?> Min { get; set; } = [];
    public Dictionary<string, decimal?> Max { get; set; } = [];
    public Dictionary<string, string?> Text { get; set; } = [];

    public string ToXml() => new XElement("s",
        Classes.Select(c => new XElement("c", new XAttribute("k", c.Key),
            Min.GetValueOrDefault(c.Key) is { } min ? new XAttribute("min", min.ToString(CultureInfo.InvariantCulture)) : null,
            Max.GetValueOrDefault(c.Key) is { } max ? new XAttribute("max", max.ToString(CultureInfo.InvariantCulture)) : null)),
        Matters.Where(m => !string.IsNullOrWhiteSpace(Text.GetValueOrDefault(m.Key))).Select(m => new XElement("t", new XAttribute("k", m.Key), Text[m.Key]!.Trim())))
        .ToString(SaveOptions.DisableFormatting);

    public static InvestmentStrategy Parse(string? xml)
    {
        var s = new InvestmentStrategy();
        if (string.IsNullOrWhiteSpace(xml))
            return s;
        try
        {
            var root = XElement.Parse(xml);
            foreach (var c in root.Elements("c"))
            {
                var k = (string?)c.Attribute("k") ?? "";
                s.Min[k] = (decimal?)c.Attribute("min");
                s.Max[k] = (decimal?)c.Attribute("max");
            }
            foreach (var t in root.Elements("t"))
                s.Text[(string?)t.Attribute("k") ?? ""] = t.Value;
        }
        catch (System.Xml.XmlException)
        {
        }
        return s;
    }

    public IEnumerable<string> Validate()
    {
        foreach (var (key, name) in Classes)
        {
            var min = Min.GetValueOrDefault(key);
            var max = Max.GetValueOrDefault(key);
            if (min is < 0 or > 100 || max is < 0 or > 100)
                yield return $"{name}: ranges are percentages from 0 to 100.";
            else if (min > max)
                yield return $"{name}: the lowest is more than the highest.";
        }
        if (Classes.Sum(c => Min.GetValueOrDefault(c.Key) ?? 0) > 100)
            yield return "The lowest percentages add up to more than 100%.";
    }
}

public sealed record SmsfYearExtras(string? EcpiMethod, decimal? ActuaryPct, string? Actuary, DateOnly? Certificate, InvestmentStrategy Strategy, DateOnly? StrategyReviewed)
{
    public static readonly SmsfYearExtras Empty = new(null, null, null, null, new InvestmentStrategy(), null);
}

/// <summary>TBAR events, ECPI details and the investment strategy (2026-10-21-20-smsf-extras.sql).</summary>
public sealed class SmsfExtrasService(IDbConnectionFactory db)
{
    public async Task<IReadOnlyList<TbarEvent>> GetEventsAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync(
                "SELECT iEventID, iMemberID, dtmEvent, strEventType, dblAmount, strNote, dtmReported FROM tblTbarEvents WHERE iUserID = @portfolioId ORDER BY dtmEvent, iEventID",
                new { portfolioId });
            return rows.Cast<IDictionary<string, object?>>().Select(r => new TbarEvent(Db.Int(r, "iEventID") ?? 0, Db.Int(r, "iMemberID") ?? 0,
                Db.Date(r, "dtmEvent") ?? default, Db.Str(r, "strEventType"), Db.Dec(r, "dblAmount") ?? 0,
                Db.Str(r, "strNote") is { Length: > 0 } n ? n : null, Db.Date(r, "dtmReported"))).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    public async Task<ProcResult> SaveEventAsync(int portfolioId, int id, int memberId, DateOnly date, string type, decimal amount, string? note, DateOnly? reported)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveTbarEvent",
            new { iUserID = portfolioId, iEventID = id, iMemberID = memberId, dtmEvent = date.ToDateTime(TimeOnly.MinValue), strEventType = type,
                dblAmount = amount, strNote = note, dtmReported = reported?.ToDateTime(TimeOnly.MinValue) },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> SetReportedAsync(int portfolioId, int id, DateOnly? reported)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspSetTbarReported",
            new { iUserID = portfolioId, iEventID = id, dtmReported = reported?.ToDateTime(TimeOnly.MinValue) }, commandType: CommandType.StoredProcedure), "Success");
    }

    public async Task<ProcResult> DeleteEventAsync(int portfolioId, int id)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteTbarEvent", new { iUserID = portfolioId, iEventID = id },
            commandType: CommandType.StoredProcedure), "Success");
    }

    public async Task<SmsfYearExtras> GetYearAsync(int portfolioId, int year)
    {
        await using var con = db.Create();
        try
        {
            var r = (await con.QueryAsync(
                    "SELECT strEcpiMethod, dblActuaryPct, strActuary, dtmCertificate, strStrategy, dtmStrategyReviewed FROM tblSmsfYearExtras WHERE iUserID = @portfolioId AND iYear = @year",
                    new { portfolioId, year }))
                .Cast<IDictionary<string, object?>>().FirstOrDefault();
            return r is null
                ? SmsfYearExtras.Empty
                : new SmsfYearExtras(Db.Str(r, "strEcpiMethod") is { Length: > 0 } m ? m : null, Db.Dec(r, "dblActuaryPct"),
                    Db.Str(r, "strActuary") is { Length: > 0 } a ? a : null, Db.Date(r, "dtmCertificate"),
                    InvestmentStrategy.Parse(Db.Str(r, "strStrategy")), Db.Date(r, "dtmStrategyReviewed"));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return SmsfYearExtras.Empty;
        }
    }

    public async Task<ProcResult> SaveEcpiAsync(int portfolioId, int year, string? method, decimal? actuaryPct, string? actuary, DateOnly? certificate)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspSaveSmsfYearExtras",
            new { iUserID = portfolioId, iYear = year, bStrategy = false, strEcpiMethod = method, dblActuaryPct = actuaryPct, strActuary = actuary,
                dtmCertificate = certificate?.ToDateTime(TimeOnly.MinValue) },
            commandType: CommandType.StoredProcedure), "Success");
    }

    public async Task<ProcResult> SaveStrategyAsync(int portfolioId, int year, InvestmentStrategy strategy, DateOnly? reviewed)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspSaveSmsfYearExtras",
            new { iUserID = portfolioId, iYear = year, bStrategy = true, strStrategy = strategy.ToXml(), dtmStrategyReviewed = reviewed?.ToDateTime(TimeOnly.MinValue) },
            commandType: CommandType.StoredProcedure), "Success");
    }
}
