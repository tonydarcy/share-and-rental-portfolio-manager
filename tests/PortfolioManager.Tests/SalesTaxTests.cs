using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.SalesTax;

namespace PortfolioManager.Tests;

public class SalesTaxTests
{
    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    private static TaxCode Code(int id, string code, string kind, decimal rate, string? boxes, string? taxBox, bool incl = true, bool active = true, int order = 0) =>
        new(id, code, code, kind, rate, boxes, taxBox, incl, active, order);

    private static SalesTaxLine Line(int codeId, decimal net, decimal tax, int journal = 1) =>
        new(journal, new DateOnly(2026, 8, 1), "x", "ManualAllocation", "Account", codeId, net, tax);

    // Australian BAS codes, as the preset adds them
    private static readonly TaxCode[] Bas =
    [
        Code(1, "GST", TaxKind.Sales, 10, "G1", "1A"),
        Code(2, "EXP", TaxKind.Sales, 0, "G1,G2", null),
        Code(3, "GSTP", TaxKind.Purchases, 10, "G11", "1B"),
        Code(4, "CAP", TaxKind.Purchases, 10, "G10", "1B"),
    ];

    [Theory]
    [InlineData(110, 10, 10)]
    [InlineData(100, 10, 9.09)]
    [InlineData(115, 15, 15)]
    [InlineData(120, 20, 20)]
    [InlineData(50, 0, 0)]
    public void Works_out_the_tax_inside_an_amount_that_includes_it(decimal gross, decimal rate, decimal tax) =>
        Assert.Equal(tax, TaxMath.TaxInGross(gross, rate));

    [Fact]
    public void Works_out_the_tax_on_an_amount_before_tax()
    {
        Assert.Equal(10m, TaxMath.TaxOnNet(100m, 10m));
        Assert.Equal(2.25m, TaxMath.TaxOnNet(9.99m, 22.5m));
    }

    [Fact]
    public void The_report_adds_sales_and_purchases_into_bas_boxes_including_gst()
    {
        var report = SalesTaxReport.Build("GST", new(2026, 7, 1), new(2026, 9, 30), Bas,
        [
            Line(1, 1000m, 100m),
            Line(1, -100m, -10m),   // a refund given
            Line(2, 500m, 0m),
            Line(3, 200m, 20m),
            Line(4, 3000m, 300m),
        ]);

        Assert.Equal(90m, report.TaxOnSales);
        Assert.Equal(320m, report.TaxOnPurchases);
        Assert.Equal(-230m, report.NetTax); // a refund
        Assert.Equal(["G1", "G2", "G10", "G11", "1A", "1B"], report.Boxes.Select(b => b.Box));
        decimal Box(string b) => report.Boxes.Single(x => x.Box == b).Amount;
        Assert.Equal(1490m, Box("G1"));   // 990 with GST + 500 GST-free
        Assert.Equal(500m, Box("G2"));
        Assert.Equal(3300m, Box("G10"));
        Assert.Equal(220m, Box("G11"));
        Assert.Equal(90m, Box("1A"));
        Assert.Equal(320m, Box("1B"));
        Assert.Equal(["GST", "EXP"], report.Boxes.Single(x => x.Box == "G1").Codes);
    }

    [Fact]
    public void Boxes_before_tax_use_the_amount_without_tax()
    {
        TaxCode[] vat = [Code(1, "SR", TaxKind.Sales, 20, "6", "1", incl: false), Code(2, "SRP", TaxKind.Purchases, 20, "7", "4", incl: false)];
        var report = SalesTaxReport.Build("VAT", new(2026, 4, 1), new(2026, 6, 30), vat, [Line(1, 1000m, 200m), Line(2, 100m, 20m)]);

        Assert.Equal(["1", "4", "6", "7"], report.Boxes.Select(b => b.Box));
        Assert.Equal([200m, 20m, 1000m, 100m], report.Boxes.Select(b => b.Amount));
        Assert.Equal(180m, report.NetTax);
    }

    [Fact]
    public void Every_box_an_active_code_names_shows_even_when_empty()
    {
        var report = SalesTaxReport.Build("GST", new(2026, 7, 1), new(2026, 9, 30),
            [.. Bas, Code(5, "OLD", TaxKind.Sales, 10, "G99", null, active: false)], [Line(1, 10m, 1m)]);

        Assert.Contains(report.Boxes, b => b.Box == "G10" && b.Amount == 0);
        Assert.DoesNotContain(report.Boxes, b => b.Box == "G99");
        Assert.Single(report.Codes);
    }

    [Fact]
    public void Lines_for_codes_of_another_portfolio_are_left_out()
    {
        var report = SalesTaxReport.Build("GST", new(2026, 7, 1), new(2026, 9, 30), Bas, [Line(1, 10m, 1m), Line(99, 500m, 50m)]);

        Assert.Single(report.Lines);
        Assert.Equal(1m, report.TaxOnSales);
    }

    [Fact]
    public void Box_names_sort_the_way_returns_print_them()
    {
        string[] boxes = ["1B", "G10", "Tax due", "G2", "1A", "G1", "14", "4", "2A", "Gross sales"];
        Assert.Equal(["G1", "G2", "G10", "1A", "1B", "2A", "4", "14", "Gross sales", "Tax due"],
            boxes.Order(BoxComparer.Instance));
    }

    [Fact]
    public void The_csv_has_the_summary_boxes_codes_and_transactions()
    {
        var report = SalesTaxReport.Build("GST", new(2026, 7, 1), new(2026, 9, 30), Bas, [Line(1, 1000m, 100m), Line(3, 200m, 20m)]);
        var text = System.Text.Encoding.UTF8.GetString(report.ToCsv()).TrimStart('﻿');

        Assert.StartsWith("GST report 2026-07-01 to 2026-09-30", text);
        Assert.Contains("Net amount to pay,80.00", text);
        Assert.Contains("1A,100.00,GST", text);
        Assert.Contains("G11,220.00,GSTP", text);
        Assert.Contains("2026-08-01,1,x,Account,GST,1000.00,100.00,1100.00", text);
    }

    [Fact]
    public void Maps_lines_turning_sales_round_so_a_sale_is_positive()
    {
        var lines = SalesTaxService.MapLines([
            Row(("iJournalID", 5), ("dtmTransactionDate", new DateTime(2026, 8, 3)), ("strJournalDescription", "Invoice 12"), ("strDescription", ""),
                ("strSourceModule", "ManualJournal"), ("iTaxCodeID", 1), ("iAccountNumber", 4400), ("strAccountName", "Rental Income"), ("Net", -1000m), ("Tax", -100m)),
            Row(("iJournalID", 6), ("dtmTransactionDate", new DateTime(2026, 8, 4)), ("strDescription", "Accountant"),
                ("iTaxCodeID", 3), ("strAccountName", "Fees"), ("Net", 200m), ("Tax", 20m)),
        ], Bas);

        Assert.Equal(1000m, lines[0].Net);
        Assert.Equal(100m, lines[0].Tax);
        Assert.Equal("Invoice 12", lines[0].Description);
        Assert.Equal("4400 - Rental Income", lines[0].AccountName);
        Assert.Equal(200m, lines[1].Net);
        Assert.Equal(20m, lines[1].Tax);
    }

    [Fact]
    public void Maps_codes_and_setup()
    {
        var codes = SalesTaxService.MapCodes([
            Row(("iTaxCodeID", 1), ("strCode", "GST"), ("strName", "GST on sales"), ("strKind", "Sales"), ("dblRate", 10m), ("strAmountBoxes", "G1"),
                ("strTaxBox", "1A"), ("bAmountIncludesTax", true), ("bIsActive", true), ("iSortOrder", 10), ("bInUse", true)),
            Row(("iTaxCodeID", 2), ("strCode", "GSTP"), ("strName", "GST on purchases"), ("strKind", "Purchases"), ("dblRate", 10m), ("strAmountBoxes", ""), ("bIsActive", false)),
        ]);
        Assert.Equal("GST - GST on sales (10%)", codes[0].Display);
        Assert.True(codes[0].InUse);
        Assert.Null(codes[1].AmountBoxes);
        Assert.False(codes[1].IsActive);
        Assert.Equal(TaxKind.Purchases, codes[1].Kind);

        var setup = SalesTaxService.MapSetup(Row(("strTaxName", "VAT"), ("iReportingMonths", 3), ("iControlAccountID", 40), ("iAccountNumber", 2300), ("strAccountName", "VAT Control")));
        Assert.Equal(new SalesTaxSetup("VAT", 3, 40, "2300 - VAT Control"), setup);
        Assert.Null(SalesTaxService.MapSetup(null));
    }

    [Fact]
    public void Every_preset_is_valid_and_has_unique_codes()
    {
        foreach (var preset in SalesTaxPresets.All.Append(SalesTaxPresets.For("XX")))
        {
            var inputs = SalesTaxService.PresetInputs(preset);
            Assert.Equal(inputs.Count, inputs.Select(i => i.Code).Distinct().Count());
            foreach (var input in inputs)
                Assert.Empty(input.Validate([]));
            Assert.Contains(preset.ReportingMonths, SalesTaxSetup.PeriodChoices);
        }
    }

    [Fact]
    public void Australia_gets_gst_and_the_bas_others_get_their_own_tax()
    {
        var au = SalesTaxPresets.For("AU");
        Assert.Equal("GST", au.TaxName);
        Assert.True(au.AmountsIncludeTax);
        Assert.Contains(au.Codes, c => c.Code == "GST" && c.Rate == 10 && c.AmountBoxes == "G1" && c.TaxBox == "1A");

        Assert.Equal("VAT", SalesTaxPresets.For("gb").TaxName);
        Assert.False(SalesTaxPresets.For("GB").AmountsIncludeTax);
        Assert.Equal(15m, SalesTaxPresets.For("NZ").Codes[0].Rate);
        Assert.Equal("Sales tax", SalesTaxPresets.TaxName("HK"));
        Assert.Equal("Sales tax", SalesTaxPresets.TaxName(null));
        // Every country the region settings offer gets a preset, even if only the plain one
        foreach (var country in RegionSettings.Countries)
            Assert.NotEmpty(SalesTaxPresets.For(country.Code).TaxName);
    }

    [Fact]
    public void Tax_code_input_is_checked()
    {
        var errors = new TaxCodeInput { Code = "gst", Name = "", Kind = "Other", Rate = 120 }.Validate([Bas[0]]);
        Assert.Contains("There is already a tax code GST.", errors);
        Assert.Contains("Enter the name of the tax code.", errors);
        Assert.Contains("Choose sales or purchases.", errors);
        Assert.Contains("Enter the rate as a percentage from 0 to 100.", errors);

        // Editing the code itself isn't a clash
        Assert.Empty(new TaxCodeInput { TaxCodeId = 1, Code = "GST", Name = "GST", Kind = TaxKind.Sales, Rate = 10 }.Validate([Bas[0]]));
    }

    [Fact]
    public void Box_lists_are_split_on_commas_and_tidied()
    {
        Assert.Equal(["G1", "G2"], TaxCode.SplitBoxes(" G1 , G2,,g1 "));
        Assert.Empty(TaxCode.SplitBoxes(null));
    }

    [Fact]
    public void Periods_follow_the_financial_year_and_the_reporting_frequency()
    {
        var quarters = TaxPeriods.ForYear(2027, 3, YearStart.July1);
        Assert.Equal(4, quarters.Count);
        Assert.Equal(new TaxPeriod(new(2026, 7, 1), new(2026, 9, 30)), quarters[0]);
        Assert.Equal(new TaxPeriod(new(2027, 4, 1), new(2027, 6, 30)), quarters[3]);

        // A UK year starts on 6 April, but VAT quarters run from the 1st
        var uk = TaxPeriods.ForYear(2027, 3, new YearStart(4, 6));
        Assert.Equal(new(2026, 4, 1), uk[0].From);
        Assert.Equal(new(2026, 6, 30), uk[0].To);

        Assert.Equal(12, TaxPeriods.ForYear(2027, 1, YearStart.July1).Count);
        Assert.Equal(new TaxPeriod(new(2026, 7, 1), new(2026, 9, 30)), TaxPeriods.LastCompleted(new(2026, 10, 16), 3, YearStart.July1));
        Assert.Equal(new TaxPeriod(new(2025, 7, 1), new(2026, 6, 30)), TaxPeriods.LastCompleted(new(2026, 10, 16), 12, YearStart.July1));
        Assert.Equal(new(2026, 10, 1), TaxPeriods.Recent(new(2026, 10, 16), 3, YearStart.July1)[0].From);
    }

    [Fact]
    public void A_journal_line_with_a_tax_code_works_out_its_tax_unless_typed()
    {
        var codes = Bas.ToDictionary(c => c.TaxCodeId);
        Assert.Equal(10m, new JournalLineInput { AccountId = 5, Debit = 110m, TaxCodeId = 3 }.TaxAmount(codes));
        Assert.Equal(9.5m, new JournalLineInput { AccountId = 5, Debit = 110m, TaxCodeId = 3, Tax = 9.5m }.TaxAmount(codes));
        Assert.Equal(0m, new JournalLineInput { AccountId = 5, Debit = 110m }.TaxAmount(codes));
    }

    [Fact]
    public void Journal_lines_with_tax_are_checked()
    {
        var codes = Bas.ToDictionary(c => c.TaxCodeId);
        var input = new JournalInput
        {
            Date = new DateTime(2026, 8, 1),
            Description = "Fees",
            Lines =
            [
                new() { AccountId = 5, Debit = 110m, TaxCodeId = 3, Tax = 200m },
                new() { AccountId = 6, Credit = 110m, TaxCodeId = 77 },
                new() { AccountId = 6, Credit = 1m, Tax = 1m },
                new() { AccountId = 5, Debit = 1m },
            ],
        };
        var errors = input.Validate(new HashSet<int> { 5, 6 }, codes);
        Assert.Contains("Line 1: the tax can't be negative or more than the amount.", errors);
        Assert.Contains("Line 2: choose a tax code from the list.", errors);
        Assert.Contains("Line 3: choose a tax code for the tax.", errors);
    }

    [Fact]
    public void Editing_a_journal_puts_the_tax_back_into_the_amount_and_hides_the_tax_line()
    {
        var journal = new JournalDetail(9, new(2026, 8, 1), "Accountant", "ManualJournal", null, null, null,
        [
            new(1, 50, "Fees", 100m, 0m, "Accountant", null, TaxCodeId: 3, TaxAmount: 10m),
            new(2, 60, "GST Control", 10m, 0m, "Accountant", null, TaxCodeId: 3, IsTaxLine: true),
            new(3, 70, "Bank", 0m, 110m, "Accountant", null),
            new(4, 50, "Fees", 50m, 0m, "Odd invoice", null, TaxCodeId: 3, TaxAmount: 5.5m),
        ]);
        var input = JournalService.ToInput(journal, Bas.ToDictionary(c => c.TaxCodeId));

        Assert.Equal(4, input.Lines.Count); // three lines and a blank row
        Assert.Equal(110m, input.Lines[0].Debit);
        Assert.Equal(3, input.Lines[0].TaxCodeId);
        Assert.Null(input.Lines[0].Tax);      // the rate's own figure, so left to be worked out again
        Assert.Equal(110m, input.Lines[1].Credit);
        Assert.Null(input.Lines[1].TaxCodeId);
        Assert.Equal(55.5m, input.Lines[2].Debit);
        Assert.Equal(5.5m, input.Lines[2].Tax); // typed by hand, so kept
    }

    [Fact]
    public void A_rule_sends_its_tax_code_only_when_it_has_one()
    {
        var with = AllocationRuleService.SaveParameters(7, new AllocationRuleInput { Contains = "ACME", AccountId = 5, TaxCodeId = 3 });
        var without = AllocationRuleService.SaveParameters(7, new AllocationRuleInput { Contains = "ACME", AccountId = 5 });

        Assert.Contains("iTaxCodeID", with.ParameterNames);
        Assert.DoesNotContain("iTaxCodeID", without.ParameterNames);
    }

    [Fact]
    public void Resolves_the_report_period()
    {
        var periods = TaxPeriods.Recent(new(2026, 10, 16), 3, YearStart.July1);
        var last = TaxPeriods.LastCompleted(new(2026, 10, 16), 3, YearStart.July1);

        Assert.Equal((new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30), (string?)null),
            TaxPeriods.Resolve("2026-04-01", new(2020, 1, 1), new(2020, 2, 1), periods, last));
        Assert.Equal((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), (string?)null),
            TaxPeriods.Resolve(null, new(2026, 8, 1), new(2026, 8, 31), periods, last));
        var (from, _, warning) = TaxPeriods.Resolve("", new(2026, 9, 1), new(2026, 8, 1), periods, last);
        Assert.Equal(last.From, from);
        Assert.NotNull(warning);
    }
}
