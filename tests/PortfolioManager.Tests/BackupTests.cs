using System.Text.Json;
using System.Text.RegularExpressions;
using PortfolioManager.Core.Backup;
using PortfolioManager.Web.Pages.Portfolio;

namespace PortfolioManager.Tests;

public class BackupTests
{
    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PortfolioManager.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, relative);
    }

    private static IEnumerable<string> DatabaseScripts() =>
        Directory.GetFiles(RepoFile("Database/Migrations"), "*.sql").Prepend(RepoFile("Database/CreateDatabase.sql"));

    [Fact]
    public void Every_table_in_the_database_is_backed_up_or_left_out_on_purpose()
    {
        var created = DatabaseScripts()
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"CREATE\s+TABLE\s+(?:\[?dbo\]?\.)?\[?(tbl\w+)", RegexOptions.IgnoreCase))
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.NotEmpty(created);
        var unplanned = created.Where(t => BackupPlan.Find(t) is null && !BackupPlan.LeftOut.ContainsKey(t)).ToList();
        Assert.True(unplanned.Count == 0, "Add these tables to BackupPlan.Tables or BackupPlan.LeftOut: " + string.Join(", ", unplanned));
        Assert.DoesNotContain(BackupPlan.Tables, t => BackupPlan.LeftOut.ContainsKey(t.Name));
    }

    [Fact]
    public void Each_table_points_only_at_planned_tables_that_go_in_first()
    {
        var order = BackupPlan.Tables.Select((t, i) => (t.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(BackupPlan.Tables.Count, order.Count);
        foreach (var table in BackupPlan.Tables)
        {
            Assert.True(table.Owner is not null || table.Parent is not null || table.SharedKey.Count > 0, $"{table.Name} has no way to find its rows");
            if (table.Parent is not null)
            {
                Assert.True(order[table.Parent] < order[table.Name], $"{table.Name} comes before its parent {table.Parent}");
                Assert.NotNull(BackupPlan.Find(table.Parent)!.Identity);
            }
            foreach (var id in table.Ids)
            {
                Assert.True(order.ContainsKey(id.Table), $"{table.Name}.{id.Column} points at {id.Table}, which isn't planned");
                Assert.NotNull(BackupPlan.Find(id.Table)!.Identity);
                if (!id.Later)
                    Assert.True(order[id.Table] < order[table.Name], $"{table.Name}.{id.Column} needs Later: {id.Table} goes in after it");
            }
            foreach (var target in table.Sources.SelectMany(s => s.Tables.Values).OfType<string>())
                Assert.True(order.ContainsKey(target), $"{table.Name} points at {target}, which isn't planned");
            // Ids set at the end are found again by the row's new id
            if (table.Ids.Any(i => i.Later) || table.Sources.Count > 0)
                Assert.NotNull(table.Identity);
            Assert.False(table.Last && (table.Ids.Any(i => i.Later) || table.Sources.Count > 0));
        }
    }

    [Fact]
    public void Every_journal_source_with_an_id_is_mapped()
    {
        // Journals posted with no source id, or one the restore needn't follow
        string[] noId = ["ManualJournal", "ManualAllocation", "BankImport", "Revaluation", "ForeignBank", "ExpenseClaimPayment", "Repayment", "Paid"];
        var modules = DatabaseScripts()
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"INSERT\s+INTO\s+(?:dbo\.)?\[?tblJournal\]?\s*\([^)]*\)\s*VALUES\s*\([^;]*?'([A-Z]\w+)'", RegexOptions.IgnoreCase))
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        Assert.Contains("ShareTrade", modules);
        var unmapped = modules.Where(m => !BackupPlan.JournalSources.ContainsKey(m) && !noId.Contains(m)).ToList();
        Assert.True(unmapped.Count == 0, "Add to BackupPlan.JournalSources: " + string.Join(", ", unmapped));
    }

    [Fact]
    public void Child_rows_are_found_through_their_parents()
    {
        var payments = BackupPlan.Find("tblRentalRentPayments")!;
        Assert.Equal(
            "x.[iTenancyID] IN (SELECT xp.[iTenancyID] FROM [tblRentalTenancies] xp WHERE xp.[iPropertyID] IN " +
            "(SELECT xpp.[iPropertyID] FROM [tblRentalProperties] xpp WHERE xpp.[iUserID] = @p))",
            BackupPlan.RowFilter(payments, "x"));
        Assert.Equal("x.[iUserID] IN (0, @p)", BackupPlan.RowFilter(BackupPlan.Find("tblChartOfAccounts")!, "x"));
        Assert.Contains("t.iUserId = @p", BackupPlan.RowFilter(BackupPlan.Find("tblShareDetails")!, "x"));
    }

    [Theory]
    [InlineData("iPreparedByUserID", true)]
    [InlineData("iChangedByUserID", true)]
    [InlineData("iClaimantUserID", true)]
    [InlineData("iUserID", false)]
    [InlineData("iAccountID", false)]
    public void Login_columns_are_recognised(string column, bool login) =>
        Assert.Equal(login, BackupPlan.IsLoginColumn(column));

    [Fact]
    public void Journal_sources_and_attachment_kinds_point_where_expected()
    {
        var journal = BackupPlan.Find("tblJournal")!.Sources.Single();
        Assert.Equal("tblShareTrades", journal.TableFor("ShareTrade"));
        Assert.Equal("tblInvoices", journal.TableFor("InvoiceCost"));
        Assert.True(journal.Keeps("TradingStock"));      // the id there is a year
        Assert.False(journal.Keeps("Something new"));    // unknown: cleared rather than pointing anywhere
        Assert.Null(journal.TableFor("Something new"));
        var attachment = BackupPlan.Find("tblAttachments")!.Sources.Single();
        Assert.Equal("tblShareTrades", attachment.TableFor("Trade"));
        Assert.Null(attachment.TableFor("Year"));
    }

    private static async Task<BackupFile> RoundTripAsync(BackupFile file)
    {
        using var stream = new MemoryStream();
        await file.WriteAsync(stream);
        stream.Position = 0;
        return await BackupFile.ReadAsync(stream);
    }

    [Fact]
    public async Task A_backup_reads_back_with_every_kind_of_value()
    {
        var table = new BackupTableData("tblShareTrades", ["iShareTradeId", "strStockCode", "dtmDateOfTransaction", "iQuantity", "dblOrderTotal", "bFlag", "binData", "strComment"]);
        var date = new DateTime(2025, 7, 1, 13, 45, 12, 123);
        table.Rows.Add([42, "BHP", date, 0.12345678d, 1234.5678m, true, new byte[] { 1, 2, 255 }, null]);
        var file = new BackupFile { PortfolioName = "Smith Family Trust", Source = "https://example.com", Tables = [table] };
        file.Settings["strCountryCode"] = "AU";
        file.Settings["iYearStartMonth"] = 7;

        var back = await RoundTripAsync(file);

        Assert.Equal("Smith Family Trust", back.PortfolioName);
        Assert.Equal("https://example.com", back.Source);
        Assert.Equal("AU", BackupValues.ToText(back.Settings["strCountryCode"]));
        Assert.Equal(7, BackupValues.Convert(back.Settings["iYearStartMonth"], "int"));
        var row = back.Table("TBLSHARETRADES")!.Rows.Single();
        Assert.Equal(42L, BackupValues.ToLong(row[0]));
        Assert.Equal("BHP", BackupValues.Convert(row[1], "varchar"));
        Assert.Equal(date, BackupValues.Convert(row[2], "datetime2"));
        Assert.Equal(0.12345678d, BackupValues.Convert(row[3], "float"));
        Assert.Equal(1234.5678m, BackupValues.Convert(row[4], "decimal"));
        Assert.Equal(true, BackupValues.Convert(row[5], "bit"));
        Assert.Equal(new byte[] { 1, 2, 255 }, BackupValues.Convert(row[6], "varbinary"));
        Assert.Null(BackupValues.Convert(row[7], "varchar"));
    }

    [Fact]
    public async Task Plain_json_reads_too()
    {
        var json = """{"format":"share-portfolio-manager-backup","version":1,"portfolio":"Test","tables":[{"name":"tblTaxYear","columns":["iYear"],"rows":[[2025]]}]}""";
        var file = await BackupFile.ReadAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
        Assert.Equal(1, file.RowCount);
    }

    [Theory]
    [InlineData("not json at all", "couldn't be read")]
    [InlineData("""{"format":"something-else","version":1}""", "isn't a portfolio backup")]
    [InlineData("""{"format":"share-portfolio-manager-backup","version":99}""", "newer version")]
    [InlineData("""{"format":"share-portfolio-manager-backup","version":1,"tables":[{"name":"tblTaxYear","columns":["a","b"],"rows":[[1]]}]}""", "damaged")]
    public async Task Files_that_arent_backups_are_refused_in_plain_words(string text, string expected)
    {
        var error = await Assert.ThrowsAsync<BackupFormatException>(() =>
            BackupFile.ReadAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text))));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Values_that_dont_fit_their_column_are_refused()
    {
        using var doc = JsonDocument.Parse("""["not a date"]""");
        Assert.Throws<BackupFormatException>(() => BackupValues.Convert(doc.RootElement[0], "date"));
    }

    [Fact]
    public void Repeating_lines_are_renumbered_and_unknown_ids_dropped()
    {
        var map = new IdMap();
        map.Add("tblChartOfAccounts", 10, 510);
        map.Add("tblTaxCodes", 3, 33);
        map.Add("tblTrackingOptions", 7, 77);
        var xml = """<lines><l d="Rent" q="1" p="100" a="10" t="3" t1="7" t2="8" /><l a="11" /></lines>""";

        var result = BackupValues.RemapLineXml(xml, map);

        Assert.Equal("""<lines><l d="Rent" q="1" p="100" a="510" t="33" t1="77" /><l /></lines>""", result);
        Assert.Equal("not xml", BackupValues.RemapLineXml("not xml", map));
        Assert.Null(BackupValues.RemapLineXml(null, map));
    }

    [Theory]
    [InlineData("int", 0)]
    [InlineData("bit", false)]
    [InlineData("varchar", "")]
    public void Columns_a_backup_hasnt_got_get_an_empty_value(string type, object expected) =>
        Assert.Equal(expected, BackupValues.EmptyFor(type));

    [Fact]
    public void File_names_are_safe_on_any_computer()
    {
        Assert.Equal("Smith Family Trust 2026-10-29.pmbackup", BackupFile.FileName("Smith Family Trust", new DateTime(2026, 10, 29)));
        Assert.Equal("A-B-C 2026-10-29.pmbackup", BackupFile.FileName("A/B:C", new DateTime(2026, 10, 29)));
        Assert.Equal("Portfolio 2026-10-29.pmbackup", BackupFile.FileName("  ", new DateTime(2026, 10, 29)));
    }

    [Fact]
    public void A_restored_portfolio_is_named_after_the_backup()
    {
        Assert.Equal("Smith Family Trust (restored)", BackupModel.RestoredName(" Smith Family Trust "));
        Assert.Equal("Portfolio (restored)", BackupModel.RestoredName(null));
        Assert.Equal(100, BackupModel.RestoredName(new string('x', 120)).Length);
    }

    [Fact]
    public void Old_ids_map_to_new_ones_per_table()
    {
        var map = new IdMap();
        map.Add("tblShareTrades", 5, 905);
        Assert.True(map.TryGet("TBLSHARETRADES", 5, out var id));
        Assert.Equal(905, id);
        Assert.False(map.TryGet("tblPayouts", 5, out _));
        Assert.Equal(1, map.Count("tblShareTrades"));
    }
}
