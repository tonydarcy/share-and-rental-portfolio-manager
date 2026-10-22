using System.Text;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using static PortfolioManager.Tests.TaxReconciliationTests;

namespace PortfolioManager.Tests;

public class TaxReturnLabelTests
{
    private static TaxWorkpaper Paper(string type, CgtDiscountRate rate, TaxIncomeFacts income, params Core.Statements.LedgerBalance[] ledger)
    {
        var client = ClientDetails.Unsaved(1, "Smith, Jones & Co", type);
        var events = new List<CgtEvent> { new("ASX", "BHP", new(2020, 1, 1), new(2026, 3, 1), 100, 1000, 3000) };
        var report = new CgtReturnReport(CgtDiscount.Summarise(events, rate), events, true, []);
        return TaxWorkpaperService.Assemble(client, 2026, Year(ledger), TaxYearData.Empty, report, income);
    }

    [Fact]
    public void An_individual_gets_the_dividend_interest_and_capital_gains_labels()
    {
        var w = Paper("Individual", HalfRate, new TaxIncomeFacts(100, 50, 700, 300, 0, 0, 10, 20, 0),
            Expense(6000, "Margin Loan Interest", 40), Expense(6100, "Advice Fee", 60));

        var labels = TaxReturnSchedule.For(w).Labels.ToDictionary(l => l.Code, l => l.Amount);

        Assert.Equal(100, labels["10L"]);
        Assert.Equal(10, labels["10M"]);
        Assert.Equal(50, labels["11S"]);
        Assert.Equal(700, labels["11T"]);
        Assert.Equal(300, labels["11U"]);
        Assert.Equal(20, labels["11V"]);
        Assert.Equal(2_000, labels["18H"]);
        Assert.Equal(1_000, labels["18A"]);
        Assert.Equal(40, labels["D7"]);
        Assert.Equal(60, labels["D8"]);
        Assert.False(labels.ContainsKey("18V"));
    }

    [Fact]
    public void An_smsf_gets_section_b_and_d_labels()
    {
        var w = Paper("SMSF", ThirdRate, Income(700, 300, interest: 1_000), Revenue(4200, "Dividend Income", 1_000), Revenue(4100, "Interest Income", 1_000),
            Revenue(4500, "Realised Gains on Investments", 2_000));

        var s = TaxReturnSchedule.For(w);
        var labels = s.Labels.Where(l => l.Code != "").ToDictionary(l => l.Code, l => l.Amount);

        Assert.Equal("SMSF annual return", s.ReturnName);
        Assert.Equal(1_333.33m, labels["11A"]);
        Assert.Equal(300, labels["11L"]);
        Assert.Equal(w.Tax!.GrossTax, labels["13T1"]);
        Assert.Equal(TaxCalculation.SmsfLevy, labels["13L"]);
    }

    [Fact]
    public void A_company_gets_the_calculation_statement()
    {
        var w = Paper("Company", CompanyRate, Income(700, 300), Revenue(4200, "Dividend Income", 1_000), Revenue(4500, "Realised Gains on Investments", 2_000));

        var labels = TaxReturnSchedule.For(w).Labels;

        Assert.Equal(3_000, labels.Single(l => l.Code == "A").Amount);
        Assert.Equal(900, labels.Single(l => l.Code == "B").Amount);
        Assert.Contains(labels, l => l.Description == "Amount due" && l.Amount == 600);
    }

    [Fact]
    public void The_csv_has_a_row_per_label_and_quotes_awkward_text()
    {
        var w = Paper("Trust", HalfRate, Income(interest: 100));
        var export = TaxReturnExports.Find("labels-csv")!;
        var schedule = TaxReturnSchedule.For(w);

        var text = Encoding.UTF8.GetString(export.Write(schedule)).TrimStart('﻿');
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.StartsWith("Return,Year,Client,ABN,Section,Label,Description,Amount,Source,Check", lines[0]);
        Assert.Equal(schedule.Labels.Count + 1, lines.Length);
        Assert.Contains("\"Smith, Jones & Co\"", lines[1]);
        Assert.Equal("Smith--Jones---Co-2025-26-tax-labels.csv", export.FileName(schedule));
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("plain", "plain")]
    public void Csv_values_are_escaped(string value, string expected) => Assert.Equal(expected, LabelSummaryCsv.Csv(value));
}
