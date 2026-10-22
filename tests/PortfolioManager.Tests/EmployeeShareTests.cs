using PortfolioManager.Core.Clients;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using static PortfolioManager.Tests.TaxReconciliationTests;

namespace PortfolioManager.Tests;

public class EmployeeShareTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);

    [Fact]
    public void Exercised_options_make_no_event_and_the_shares_start_on_the_exercise_date()
    {
        // 1,000 options bought for $200, exercised for 1,000 shares at $1: sold for their cost, shares bought at $1.20
        TradeLot[] trades =
        [
            new(1, "ASX", "XYZO", "Buy", new DateOnly(2024, 1, 1), 1000, 0.2m),
            new(2, "ASX", "XYZO", "Sell", new DateOnly(2026, 8, 1), 1000, 0.2m),
            new(3, "ASX", "XYZ", "Buy", new DateOnly(2026, 8, 1), 1000, 1.2m),
        ];
        var lot = new CapitalReturn("ASX", "XYZO", new DateOnly(2026, 8, 1), 0m,
            Transfer: new ParcelTransfer(ParcelTransfer.OptionExercise, "ASX", "XYZ", 1m, 1m, 1000m, BuyTradeId: 3, SellTradeId: 2));
        var m = CgtParcels.Match(trades, null, [lot]);

        Assert.Empty(m.Events);
        var p = Assert.Single(m.Open);
        Assert.Equal(("XYZ", new DateOnly(2026, 8, 1), 1200m), (p.StockCode, p.Acquired, p.TotalCost));
    }

    [Fact]
    public void Lapsed_options_sold_for_nil_are_a_loss_of_their_cost()
    {
        TradeLot[] trades =
        [
            new(1, "ASX", "XYZO", "Buy", new DateOnly(2024, 1, 1), 1000, 0.2m),
            new(2, "ASX", "XYZO", "Sell", new DateOnly(2026, 8, 1), 1000, 0m),
        ];
        Assert.Equal(-200m, Assert.Single(CgtParcels.Match(trades).Events).Gain);
    }

    [Fact]
    public void Exercise_form_and_preview_take_the_oldest_options()
    {
        var r = ParcelTransferEntry.Validate(new TransferForm
        {
            Kind = "OptionExercise", Market = "ASX", Code = "XYZO", Date = new DateOnly(2026, 8, 1), NewCode = "XYZ", NewUnits = 600, Units = 600, ExercisePrice = 1,
        }, Today);
        Assert.True(r.IsValid);
        Assert.Equal("XYZ", r.Value!.NewCode);

        OpenParcel[] open = [new("ASX", "XYZO", new DateOnly(2024, 1, 1), 500, 0.2m), new("ASX", "XYZO", new DateOnly(2025, 1, 1), 500, 0.4m)];
        var p = ParcelTransferEntry.Preview(open, r.Value);
        Assert.Equal((600m, 140m, 1m), (p.UnitsHeld, p.CostMoved, p.CostShare));

        var missing = ParcelTransferEntry.Validate(new TransferForm { Kind = "OptionExercise", Market = "ASX", Code = "XYZO", Date = new DateOnly(2026, 8, 1) }, Today);
        Assert.Contains(missing.Errors, e => e.Field == nameof(TransferForm.ExercisePrice));
        Assert.Contains(missing.Errors, e => e.Field == nameof(TransferForm.NewCode));
    }

    [Fact]
    public void Statements_add_up_by_year_and_are_checked()
    {
        EssStatement[] s =
        [
            new(1, 2026, "Acme", 1500, 0, 2000, 0, 300, 100),
            new(2, 2026, "Beta", 0, 400, 0, 50, 0, 0),
            new(3, 2025, "Acme", 999, 0, 0, 0, 0, 0),
        ];
        var y = EssYear.For(s, 2026);
        Assert.Equal((1500m, 400m, 2000m, 50m, 300m, 100m, 3950m),
            (y.UpfrontEligible, y.UpfrontNotEligible, y.Deferral, y.Pre2009, y.ForeignSource, y.TfnWithheld, y.TotalDiscount));

        Assert.Empty(EssStatements.Validate(s[0]));
        Assert.NotEmpty(EssStatements.Validate(s[0] with { Employer = " " }));
        Assert.NotEmpty(EssStatements.Validate(s[0] with { ForeignSource = 5000 }));
        Assert.NotEmpty(EssStatements.Validate(new EssStatement(0, 2026, "Acme", 0, 0, 0, 0, 0, 0)));
    }

    [Fact]
    public void An_individual_gets_the_item_12_labels_and_a_company_a_warning()
    {
        var ess = new EssYear(1500, 400, 2000, 50, 300, 100);
        var events = new List<CgtEvent>();
        var report = new CgtReturnReport(CgtDiscount.Summarise(events, HalfRate), events, true, []);
        var w = TaxWorkpaperService.Assemble(ClientDetails.Unsaved(1, "Jo", "Individual"), 2026, Year(), TaxYearData.Empty, report, TaxIncomeFacts.None, ess: ess);
        var labels = TaxReturnSchedule.For(w).Labels.ToDictionary(l => l.Code, l => l.Amount);

        Assert.Equal((1500m, 400m, 2000m, 50m, 100m, 300m), (labels["12D"], labels["12E"], labels["12F"], labels["12G"], labels["12C"], labels["12A"]));

        var company = TaxWorkpaperService.Assemble(ClientDetails.Unsaved(1, "Co", "Company"), 2026, Year(), TaxYearData.Empty, report, TaxIncomeFacts.None, ess: ess);
        Assert.Contains(company.Warnings, x => x.Contains("Employee share scheme"));
        Assert.DoesNotContain(TaxReturnSchedule.For(company).Labels, l => l.Code.StartsWith("12"));
    }
}
