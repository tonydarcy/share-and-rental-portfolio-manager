using PortfolioManager.Mobile.Client;

namespace PortfolioManager.Mobile.Pages;

/// <summary>Dividends, distributions and interest received in a period, newest first, with the period's totals.</summary>
public sealed class PayoutsPage : PortfolioPage
{
    private readonly Picker period = new() { Title = "Period", ItemsSource = ReportPeriod.Choices.ToList(), SelectedIndex = 0 };
    private readonly VerticalStackLayout body = new() { Padding = new Thickness(16, 8) };
    private readonly VerticalStackLayout figures = [];

    public PayoutsPage(Session session) : base(session, "Payouts")
    {
        body.Add(Header);
        body.Add(period);
        body.Add(figures);
        period.SelectedIndexChanged += (_, _) => Refresh.IsRefreshing = true;
        Refresh.Content = new ScrollView { Content = body };
    }

    protected override async Task ShowAsync(SiteApi api, PortfolioSummary portfolio, Func<bool> stillWanted)
    {
        var (from, to) = ReportPeriod.Range(period.SelectedIndex, DateOnly.FromDateTime(DateTime.Today));
        var list = await api.ListPayoutsAsync(portfolio.PortfolioId, from, to);
        if (!stillWanted())
            return;

        figures.Clear();
        var t = list.Totals;
        figures.Add(new Label { Text = $"{from:d MMM yyyy} to {to:d MMM yyyy}", FontSize = 13, TextColor = Colors.Gray, Margin = new Thickness(0, 8, 0, 0) });
        figures.Add(new Label { Text = Ui.Money(t?.Total ?? 0), FontSize = 28, FontAttributes = FontAttributes.Bold });
        if (t is not null)
        {
            if (t.FrankingCredit != 0)
                figures.Add(Ui.Line("Franking credits", Ui.Money(t.FrankingCredit)));
            if (t.TaxWithheld != 0)
                figures.Add(Ui.Line("Tax withheld", Ui.Money(t.TaxWithheld)));
            if (t.ForeignTax != 0)
                figures.Add(Ui.Line("Foreign tax", Ui.Money(t.ForeignTax)));
        }

        figures.Add(Ui.Heading("PAYOUTS"));
        if (list.Payouts.Rows.Count == 0)
            figures.Add(new Label { Text = "No payouts in this period.", TextColor = Colors.Gray });
        foreach (var p in list.Payouts.Rows)
        {
            var detail = p.Date is { } d ? d.ToString("d MMM yyyy") : "";
            if (p.FrankingCredit != 0)
                detail += $"  ·  franking {Ui.Money(p.FrankingCredit)}";
            figures.Add(Ui.Line(p.StockCode, Ui.Money(p.Total), null, detail));
        }
        if (list.Payouts.Returned < list.Payouts.Total)
            figures.Add(new Label { Text = $"Showing the newest {list.Payouts.Returned} of {list.Payouts.Total}. See the site for the rest.", FontSize = 12, TextColor = Colors.Gray });
    }
}
