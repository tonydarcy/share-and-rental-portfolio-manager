using System.Text;
using PortfolioManager.Core.Exports;

namespace PortfolioManager.Tests;

public class LedgerExportTests
{
    private static readonly ExportLine[] Lines =
    [
        new(1, new DateOnly(2026, 1, 5), "Dividend BHP", 10, 1100, "Bank", 70, 0, null),
        new(1, new DateOnly(2026, 1, 5), "Dividend BHP", 11, 4200, "Dividends", 0, 70, "BHP, final"),
        new(2, new DateOnly(2026, 1, 20), "Bank fee", 12, 6100, "Bank fees", 5, 0, null),
        new(2, new DateOnly(2026, 1, 20), "Bank fee", 10, 1100, "Bank", 0, 5, null),
        new(3, new DateOnly(2026, 2, 3), "Interest", 10, 1100, "Bank", 1.5m, 0, null),
        new(3, new DateOnly(2026, 2, 3), "Interest", 13, 4300, "Interest", 0, 1.5m, null),
    ];

    private static readonly Dictionary<int, ExportMapping> Map = new() { [11] = new(11, "260", "GST Free Income") };

    [Fact]
    public void Every_journal_goes_across_with_mapped_codes()
    {
        var j = LedgerExportFiles.Build(Lines, ExportShapes.Journals, ExportSystems.Xero, new DateOnly(2026, 6, 30), Map);
        Assert.Equal(3, j.Count);
        Assert.Equal("Dividend BHP (#1)", j[0].Narration);
        Assert.Equal(("1100", "BAS Excluded", "Dividend BHP", 70m), j[0].Lines[0]);
        Assert.Equal(("260", "GST Free Income", "BHP, final", -70m), j[0].Lines[1]);
        Assert.All(j, x => Assert.Equal(0, x.Lines.Sum(l => l.Amount)));
    }

    [Fact]
    public void Monthly_summary_nets_each_account()
    {
        var j = LedgerExportFiles.Build(Lines, ExportShapes.Monthly, ExportSystems.Myob, new DateOnly(2026, 6, 30), Map);
        Assert.Equal(2, j.Count);
        Assert.Equal(new DateOnly(2026, 1, 31), j[0].Date);
        Assert.Contains(j[0].Lines, l => l.Code == "1100" && l.Amount == 65 && l.Tax == "N-T");
    }

    [Fact]
    public void Balances_make_one_journal_and_leave_out_nil_accounts()
    {
        var lines = Lines.Append(new(4, new DateOnly(2026, 3, 1), "Fee refund", 12, 6100, "Bank fees", 0, 5, null))
            .Append(new(4, new DateOnly(2026, 3, 1), "Fee refund", 10, 1100, "Bank", 5, 0, null));
        var j = Assert.Single(LedgerExportFiles.Build(lines, ExportShapes.Balances, ExportSystems.Xero, new DateOnly(2026, 6, 30), Map));
        Assert.DoesNotContain(j.Lines, l => l.Code == "6100");
        Assert.Equal(0, j.Lines.Sum(l => l.Amount));
    }

    [Fact]
    public void Xero_file_has_its_headings_and_signed_amounts()
    {
        var text = Encoding.UTF8.GetString(LedgerExportFiles.Xero(LedgerExportFiles.Build(Lines, ExportShapes.Journals, ExportSystems.Xero, new DateOnly(2026, 6, 30), Map)));
        var rows = text.TrimStart('﻿').Split("\r\n".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("*Narration,*Date,Description,*AccountCode,*TaxRate,*Amount", rows[0]);
        Assert.Equal("Dividend BHP (#1),05/01/2026,\"BHP, final\",260,GST Free Income,-70.00,,,,", rows[2]);
    }

    [Fact]
    public void Myob_file_is_tab_delimited_with_a_blank_line_between_journals()
    {
        var text = Encoding.UTF8.GetString(LedgerExportFiles.Myob(LedgerExportFiles.Build(Lines, ExportShapes.Journals, ExportSystems.Myob, new DateOnly(2026, 6, 30), Map)));
        var rows = text.Replace("\r\n", "\n").Split('\n');
        Assert.Equal("Journal Number", rows[0].Split('\t')[0]);
        Assert.Equal(["PM00001", "05/01/2026", "Dividend BHP (#1)", "1100", "70.00", "70.00", "", "", "N-T", "Dividend BHP"], rows[1].Split('\t'));
        Assert.Equal("", rows[3]);
    }
}
