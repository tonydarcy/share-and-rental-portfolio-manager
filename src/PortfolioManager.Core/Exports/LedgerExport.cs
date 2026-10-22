using System.Data;
using System.Globalization;
using System.Text;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Exports;

public static class ExportSystems
{
    public const string Xero = "Xero", Myob = "MYOB";
    public static readonly IReadOnlyList<string> All = [Xero, Myob];

    public static string DefaultTax(string system) => system == Myob ? "N-T" : "BAS Excluded";
}

public static class ExportShapes
{
    /// <summary>Every journal; one journal of each month's net movement per account; or the balances at the end date as one opening journal.</summary>
    public const string Journals = "Journals", Monthly = "Monthly", Balances = "Balances";

    public static string Label(string s) => s switch
    {
        Journals => "Every journal",
        Monthly => "One journal a month (net movement in each account)",
        Balances => "Balances at the end date (an opening balance journal)",
        _ => s,
    };
}

/// <summary>A journal line ready to export.</summary>
public sealed record ExportLine(int JournalId, DateOnly Date, string Narration, int AccountId, int AccountNumber, string AccountName,
    decimal Debit, decimal Credit, string? Description)
{
    public decimal Amount => Debit - Credit;
}

public sealed record ExportMapping(int AccountId, string Code, string? TaxCode);

/// <summary>One journal of the export: lines with the code and tax code they go out under.</summary>
public sealed record ExportJournal(string Narration, DateOnly Date, IReadOnlyList<(string Code, string Tax, string Description, decimal Amount)> Lines);

public static class LedgerExportFiles
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Groups the lines into journals in the chosen shape and applies the account mapping. Accounts netting to nil are left out.</summary>
    public static IReadOnlyList<ExportJournal> Build(IEnumerable<ExportLine> lines, string shape, string system, DateOnly to,
        IReadOnlyDictionary<int, ExportMapping> map)
    {
        string Code(ExportLine l) => map.TryGetValue(l.AccountId, out var m) && m.Code.Length > 0 ? m.Code : l.AccountNumber.ToString(Inv);
        string Tax(ExportLine l) => map.TryGetValue(l.AccountId, out var m) && m.TaxCode is { Length: > 0 } t ? t : ExportSystems.DefaultTax(system);

        IEnumerable<(string Code, string Tax, string Description, decimal Amount)> Net(IEnumerable<ExportLine> group, Func<ExportLine, string> describe) =>
            group.GroupBy(l => (Code: Code(l), Tax: Tax(l)))
                .Select(g => (g.Key.Code, g.Key.Tax, describe(g.First()), Math.Round(g.Sum(l => l.Amount), 2)))
                .Where(x => x.Item4 != 0)
                .OrderBy(x => x.Code, StringComparer.Ordinal);

        var all = lines.ToList();
        IReadOnlyList<ExportJournal> journals = shape switch
        {
            ExportShapes.Monthly => all.GroupBy(l => new DateOnly(l.Date.Year, l.Date.Month, 1))
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var end = g.Key.AddMonths(1).AddDays(-1);
                    return new ExportJournal($"Portfolio Manager summary {g.Key:MMM yyyy}", end > to ? to : end, [.. Net(g, l => l.AccountName)]);
                })
                .ToList(),
            ExportShapes.Balances => [new ExportJournal($"Opening balances at {to:dd/MM/yyyy}", to, [.. Net(all, l => l.AccountName)])],
            _ => all.GroupBy(l => (l.JournalId, l.Date, l.Narration))
                .OrderBy(g => g.Key.Date).ThenBy(g => g.Key.JournalId)
                .Select(g => new ExportJournal($"{g.Key.Narration} (#{g.Key.JournalId})", g.Key.Date,
                    [.. g.Select(l => (Code(l), Tax(l), string.IsNullOrWhiteSpace(l.Description) ? g.Key.Narration : l.Description!, Math.Round(l.Amount, 2)))
                        .Where(x => x.Item4 != 0)]))
                .ToList(),
        };
        return journals.Where(j => j.Lines.Count > 0).ToList();
    }

    /// <summary>
    /// Xero's manual journal import CSV: one row per line, rows with the same narration and date make one journal,
    /// a positive amount is a debit and a negative one a credit.
    /// </summary>
    public static byte[] Xero(IReadOnlyList<ExportJournal> journals)
    {
        var sb = new StringBuilder();
        sb.AppendLine("*Narration,*Date,Description,*AccountCode,*TaxRate,*Amount,TrackingName1,TrackingOption1,TrackingName2,TrackingOption2");
        foreach (var j in journals)
            foreach (var l in j.Lines)
                sb.AppendLine(string.Join(',', Text(j.Narration), j.Date.ToString("dd/MM/yyyy", Inv), Text(l.Description), Text(l.Code), Text(l.Tax),
                    l.Amount.ToString("0.00", Inv), "", "", "", ""));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    /// <summary>
    /// MYOB AccountRight's general journal import (File, Import Data, General Journal Entries): tab-delimited text,
    /// a header row, and a blank line after each journal.
    /// </summary>
    public static byte[] Myob(IReadOnlyList<ExportJournal> journals)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join('\t', "Journal Number", "Date", "Memo", "Account Number", "Debit Ex-Tax Amount", "Debit Inc-Tax Amount",
            "Credit Ex-Tax Amount", "Credit Inc-Tax Amount", "Tax Code", "Allocation Memo"));
        var n = 0;
        foreach (var j in journals)
        {
            n++;
            foreach (var l in j.Lines)
            {
                var debit = l.Amount > 0 ? l.Amount.ToString("0.00", Inv) : "";
                var credit = l.Amount < 0 ? (-l.Amount).ToString("0.00", Inv) : "";
                sb.AppendLine(string.Join('\t', $"PM{n:00000}", j.Date.ToString("dd/MM/yyyy", Inv), Tab(j.Narration), Tab(l.Code), debit, debit, credit, credit,
                    Tab(l.Tax), Tab(l.Description)));
            }
            sb.AppendLine();
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string Text(string value)
    {
        if (value.Length > 0 && "=+@".Contains(value[0]))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static string Tab(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}

/// <summary>Export the ledger to Xero or MYOB (2026-10-21-18-ledger-export.sql for the account mapping).</summary>
public sealed class LedgerExportService(IDbConnectionFactory db, RealisedGainService gains)
{
    public async Task<IReadOnlyDictionary<int, ExportMapping>> GetMapAsync(int portfolioId, string system)
    {
        await using var con = db.Create();
        try
        {
            return (await con.QueryAsync<ExportMapping>(
                    "SELECT iAccountID AS AccountId, strCode AS Code, strTaxCode AS TaxCode FROM tblLedgerExportMap WHERE iUserID = @portfolioId AND strSystem = @system",
                    new { portfolioId, system }))
                .ToDictionary(m => m.AccountId);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return new Dictionary<int, ExportMapping>();
        }
    }

    public async Task<ProcResult> SaveMapAsync(int portfolioId, string system, int accountId, string? code, string? taxCode)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspSaveLedgerExportMap",
            new { iUserID = portfolioId, strSystem = system, iAccountID = accountId, strCode = code ?? "", strTaxCode = taxCode },
            commandType: CommandType.StoredProcedure), "Success");
    }

    /// <summary>The journal lines dated from..to; for balances, every line up to the end date.</summary>
    public async Task<IReadOnlyList<ExportLine>> GetLinesAsync(int portfolioId, DateOnly? from, DateOnly to)
    {
        await gains.SyncAsync(portfolioId);
        await using var con = db.Create();
        var rows = await con.QueryAsync("""
            SELECT J.iJournalID AS JournalId, J.dtmTransactionDate AS [Date], ISNULL(J.strDescription, '') AS Narration,
                   L.iAccountID AS AccountId, A.iAccountNumber AS AccountNumber, A.strAccountName AS AccountName,
                   ISNULL(L.dblDebit, 0) AS Debit, ISNULL(L.dblCredit, 0) AS Credit, L.strDescription AS [Description]
            FROM tblJournal J
            JOIN tblJournalLines L ON L.iJournalID = J.iJournalID
            JOIN tblChartOfAccounts A ON A.iAccountID = L.iAccountID
            WHERE J.iUserID = @portfolioId AND J.dtmTransactionDate <= @to AND (@from IS NULL OR J.dtmTransactionDate >= @from)
            ORDER BY J.dtmTransactionDate, J.iJournalID, L.iJournalLineID
            """, new { portfolioId, from = from?.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue) });
        return rows.Cast<IDictionary<string, object?>>().Select(r => new ExportLine(
                Db.Int(r, "JournalId") ?? 0, Db.Date(r, "Date") ?? default, Db.Str(r, "Narration"), Db.Int(r, "AccountId") ?? 0,
                Db.Int(r, "AccountNumber") ?? 0, Db.Str(r, "AccountName").Trim(), Db.Dec(r, "Debit") ?? 0, Db.Dec(r, "Credit") ?? 0,
                Db.Str(r, "Description") is { Length: > 0 } d ? d : null))
            .ToList();
    }
}
