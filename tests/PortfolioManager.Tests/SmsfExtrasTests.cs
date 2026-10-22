using PortfolioManager.Core.TaxWork;

namespace PortfolioManager.Tests;

public class SmsfExtrasTests
{
    [Theory]
    [InlineData(2026, 7, 1, 2026, 10, 28)]
    [InlineData(2026, 9, 30, 2026, 10, 28)]
    [InlineData(2026, 12, 15, 2027, 1, 28)]
    [InlineData(2027, 2, 1, 2027, 4, 28)]
    public void Tbar_is_due_28_days_after_the_quarter(int y, int m, int d, int dy, int dm, int dd) =>
        Assert.Equal(new DateOnly(dy, dm, dd), Tbar.DueDate(new DateOnly(y, m, d)));

    [Fact]
    public void Transfer_balance_is_credits_less_debits_to_the_date()
    {
        var events = new[]
        {
            new TbarEvent(1, 7, new DateOnly(2024, 7, 1), TbarEventTypes.Commenced, 1_500_000, null, null),
            new TbarEvent(2, 7, new DateOnly(2025, 3, 1), TbarEventTypes.Commutation, 200_000, null, null),
            new TbarEvent(3, 7, new DateOnly(2026, 8, 1), TbarEventTypes.Commenced, 100_000, null, null),
        };
        Assert.Equal(1_300_000m, Tbar.Balances(events, new DateOnly(2026, 6, 30))[7]);
    }

    private static SmsfMemberAccount Account(string phase) => new(1, 1, 2026, phase, 100_000, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void Ecpi_method_follows_the_phases()
    {
        var member = new SmsfMember(1, "Jane", null);
        Assert.Null(EcpiMethods.Suggest(new SmsfYearData([member], [Account(SmsfPhases.Accumulation)]), 2026).Method);
        Assert.Equal(EcpiMethods.Segregated, EcpiMethods.Suggest(new SmsfYearData([member], [Account(SmsfPhases.Retirement)]), 2026).Method);
        var mixed = EcpiMethods.Suggest(new SmsfYearData([member], [Account(SmsfPhases.Retirement), Account(SmsfPhases.Accumulation)]), 2026);
        Assert.Equal(EcpiMethods.Proportionate, mixed.Method);
        Assert.True(mixed.NeedsCertificate);
    }

    [Fact]
    public void Strategy_round_trips_and_checks_its_ranges()
    {
        var s = new InvestmentStrategy();
        s.Min["AusShares"] = 40; s.Max["AusShares"] = 80; s.Text["Objectives"] = "CPI + 3%";
        var back = InvestmentStrategy.Parse(s.ToXml());
        Assert.Equal(40m, back.Min["AusShares"]);
        Assert.Equal(80m, back.Max["AusShares"]);
        Assert.Equal("CPI + 3%", back.Text["Objectives"]);
        Assert.Empty(back.Validate());
        back.Min["Cash"] = 90;
        Assert.Contains(back.Validate(), e => e.Contains("more than 100%"));
        back.Max["Cash"] = 50;
        Assert.Contains(back.Validate(), e => e.Contains("Cash"));
        Assert.Empty(InvestmentStrategy.Parse("not xml").Min);
    }
}
