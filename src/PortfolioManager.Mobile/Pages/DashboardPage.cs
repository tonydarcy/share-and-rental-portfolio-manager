using PortfolioManager.Mobile.Client;

namespace PortfolioManager.Mobile.Pages;

/// <summary>The headline figures: total value, the latest day's move, gain, the biggest holdings and payouts on the way.</summary>
public sealed class DashboardPage : PortfolioPage
{
    private readonly VerticalStackLayout body = new() { Padding = new Thickness(16, 8) };

    public DashboardPage(Session session) : base(session, "Dashboard")
    {
        body.Add(Header);
        Refresh.Content = new ScrollView { Content = body };
    }

    protected override void ClearFigures()
    {
        // Keep the header (picker, notes), remove the rest
        while (body.Count > 1)
            body.RemoveAt(body.Count - 1);
    }

    protected override async Task ShowAsync(SiteApi api, PortfolioSummary portfolio, Func<bool> stillWanted)
    {
        var d = await api.GetDashboardAsync(portfolio.PortfolioId);
        if (!stillWanted())
            return;

        ClearFigures();

        body.Add(new Label { Text = "Total value" + (d.Currency is { Length: > 0 } c ? $" ({c})" : ""), FontSize = 13, TextColor = Colors.Gray });
        body.Add(new Label { Text = Ui.Money(d.TotalPortfolioValue), FontSize = 32, FontAttributes = FontAttributes.Bold });
        if (d.LatestMove is { } move)
            body.Add(new Label
            {
                Text = $"{Ui.Signed(move.Change)} ({Ui.Percent(move.Return)}) on {move.Date:d MMM}",
                TextColor = Ui.Shade(move.Change), FontSize = 15,
            });

        body.Add(Ui.Heading("SUMMARY"));
        body.Add(Ui.Line("Shares", Ui.Money(d.TotalShareValue)));
        body.Add(Ui.Line("Cash", Ui.Money(d.TotalCashValue)));
        body.Add(Ui.Line("Cost of holdings", Ui.Money(d.Holdings.Cost)));
        body.Add(Ui.Line("Gain on holdings", Ui.Signed(d.Holdings.CapitalGain), Ui.Shade(d.Holdings.CapitalGain)));
        body.Add(Ui.Line("Payouts received", Ui.Money(d.Holdings.Payouts)));

        if (d.TopHoldings.Count > 0)
        {
            body.Add(Ui.Heading("BIGGEST HOLDINGS"));
            foreach (var h in d.TopHoldings)
                body.Add(Ui.Line(h.StockCode, Ui.Money(h.MarketValue), null,
                        $"{h.Weight:0.0%} of shares" + (h.GainPercent is { } g ? $"  ·  gain {g:+0.0%;-0.0%;0.0%}" : ""))
                    .OnTap(() => OpenShareAsync(h.ExchangeCode, h.StockCode)));
        }

        body.Add(Ui.Heading("PAYOUTS EXPECTED IN THE NEXT 90 DAYS"));
        if (d.UpcomingPayouts.Count == 0)
            body.Add(new Label { Text = "None expected.", TextColor = Colors.Gray });
        foreach (var p in d.UpcomingPayouts)
            body.Add(Ui.Line(p.StockCode, Ui.Money(p.ProjectedAmount), null, $"about {p.ProjectedPaymentDate:d MMM yyyy}"));
        body.Add(new Label
        {
            Text = "Expected payouts are estimates from past payments.", FontSize = 12, TextColor = Colors.Gray, Margin = new Thickness(0, 12),
        });
    }
}
