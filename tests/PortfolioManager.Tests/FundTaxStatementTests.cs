using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;

namespace PortfolioManager.Tests;

public class FundTaxStatementTests
{
    private static readonly CgtDiscountRate Individual = new(CgtEntityKind.Individual, 0.5m, false);
    private static readonly DateOnly YearEnd = new(2026, 6, 30);

    // A typical ETF year: $1,000 cash; $300 franked + $130 credits, $100 unfranked, $50 interest, $40 foreign (incl. $6 tax),
    // $200 discounted gains (so $400 gross), $20 other gains, a $96 AMIT decrease
    private static FundTaxStatement Statement(decimal decrease = 96, decimal increase = 0) =>
        new(1, "ASX", "VAS", 2026, YearEnd, 1000, 300, 130, 100, 50, 0, 0, 200, 20, 40, 6, 0, increase, decrease, null);

    private static readonly TradeLot[] Trades = [new(1, "ASX", "VAS", "Buy", new DateOnly(2024, 1, 1), 100, 90m)];

    [Fact]
    public void Statement_ties_to_the_cash()
    {
        var s = Statement();
        Assert.Equal(0m, s.Difference);
        Assert.Equal(400m, s.GrossDiscountGain);
        Assert.Equal(620m, s.AssessableIncome);
        Assert.Equal(5m, Statement(decrease: 101).Difference);
    }

    [Fact]
    public void Gains_count_and_the_decrease_lowers_the_cost_base()
    {
        var match = CgtParcels.Match(Trades, null, [Statement().ToCostBaseLot()]);
        var parcel = Assert.Single(match.Open);
        Assert.Equal(90m - 0.96m, parcel.CostPerUnit);
        Assert.Equal(2, match.Events.Count);
        Assert.All(match.Events, e => Assert.True(e.IsFundGain));
        var summary = CgtDiscountService.Build(match, Individual, FinancialYear.Resolve(2026, null, null, YearEnd)).Summary;
        Assert.Equal(400m, summary.DiscountGains);
        Assert.Equal(20m, summary.OtherGains);
        Assert.Equal(220m, summary.NetCapitalGain);
        Assert.Equal(0, match.CapitalReturns);
    }

    [Fact]
    public void An_increase_raises_the_cost_base()
    {
        var match = CgtParcels.Match(Trades, null, [Statement(decrease: 0, increase: 50).ToCostBaseLot()]);
        Assert.Equal(90.5m, Assert.Single(match.Open).CostPerUnit);
    }

    [Fact]
    public void Gains_count_even_when_sold_before_the_year_end()
    {
        var match = CgtParcels.Match([.. Trades, new TradeLot(2, "ASX", "VAS", "Sell", new DateOnly(2026, 3, 1), 100, 95m)], null, [Statement().ToCostBaseLot()]);
        Assert.Equal(420m, match.Events.Where(e => e.IsFundGain).Sum(e => e.Gain));
        Assert.Single(match.Problems);
    }

    private static Payout P(string code, decimal total, decimal franked = 0, decimal credit = 0, decimal trust = 0) =>
        new(1, "ASX", code, new DateTime(2026, 1, 15), total, franked, 0, credit, 0, trust, 0, 0, 0, 0, 0, "", true);

    [Fact]
    public void Statement_figures_replace_that_funds_payouts()
    {
        var payouts = new[] { P("VAS", 500, trust: 500), P("VAS", 500, trust: 500), P("CBA", 700, franked: 700, credit: 300) };
        var f = TaxIncomeFacts.WithStatements(payouts, [Statement()], 2026);
        Assert.Equal(700m, f.Franked);
        Assert.Equal(300m, f.TrustFranked);
        Assert.Equal(430m, f.FrankingCredits);
        Assert.Equal(130m, f.TrustFrankingCredits);
        Assert.Equal(300m, f.DividendFrankingCredits);
        Assert.Equal(150m, f.TrustIncome);
        Assert.Equal(40m, f.ForeignIncome);
        Assert.Equal(6m, f.ForeignTax);
        // Attributed $620 (with credits, without gains) less the $1,000 the accounts recorded
        Assert.Equal(-380m, f.StatementAdjustment);
        Assert.Equal(1, f.Statements);
    }

    [Fact]
    public void Other_years_statements_change_nothing()
    {
        var payouts = new[] { P("VAS", 500, trust: 500) };
        Assert.Equal(TaxIncomeFacts.From(payouts), TaxIncomeFacts.WithStatements(payouts, [Statement()], 2027));
    }

    [Fact]
    public void Both_an_increase_and_a_decrease_is_refused() =>
        Assert.Contains(FundTaxStatements.Validate(Statement(decrease: 10, increase: 5)), e => e.Contains("net AMIT"));
}
