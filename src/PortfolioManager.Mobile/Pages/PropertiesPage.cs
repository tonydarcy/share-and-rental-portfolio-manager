using PortfolioManager.Mobile.Client;

namespace PortfolioManager.Mobile.Pages;

/// <summary>Rental properties: each one's net rent for the financial year and the year's tax return totals. Tap one for its schedule.</summary>
public sealed class PropertiesPage : PortfolioPage
{
    private static readonly List<string> Years = ["This financial year", "Last financial year"];
    private readonly Picker year = new() { Title = "Year", ItemsSource = Years, SelectedIndex = 0 };
    private readonly VerticalStackLayout figures = [];

    public PropertiesPage(Session session) : base(session, "Property")
    {
        var body = new VerticalStackLayout { Padding = new Thickness(16, 8) };
        body.Add(Header);
        body.Add(year);
        body.Add(figures);
        year.SelectedIndexChanged += (_, _) => Reload();
        Refresh.Content = new ScrollView { Content = body };
    }

    protected override void ClearFigures() => figures.Clear();

    protected override async Task ShowAsync(SiteApi api, PortfolioSummary portfolio, Func<bool> stillWanted)
    {
        var chosen = year.SelectedIndex;
        var yearEnding = ReportPeriod.YearEnding(DateOnly.FromDateTime(DateTime.Today), chosen);
        var properties = await api.ListRentalPropertiesAsync(portfolio.PortfolioId);
        var schedule = properties.Count == 0 ? null : await api.GetRentalScheduleAsync(portfolio.PortfolioId, yearEnding);
        if (!stillWanted() || chosen != year.SelectedIndex)
            return;

        figures.Clear();
        if (schedule is null)
        {
            figures.Add(new Label
            {
                Text = "No rental properties yet. Add one on the site under Property > Rental properties, then its rent and expenses can be entered here.",
                TextColor = Colors.Gray, Margin = new Thickness(0, 12),
            });
            return;
        }

        var t = schedule.Item21;
        figures.Add(new Label { Text = $"Net rent, {schedule.FinancialYear ?? $"year to 30 June {yearEnding}"}", FontSize = 13, TextColor = Colors.Gray, Margin = new Thickness(0, 8, 0, 0) });
        figures.Add(new Label { Text = Ui.Money(t.NetRent), FontSize = 28, FontAttributes = FontAttributes.Bold, TextColor = Ui.Shade(t.NetRent) });
        figures.Add(Ui.Line("Gross rent", Ui.Money(t.GrossRent21P)));
        figures.Add(Ui.Line("Interest", Ui.Money(t.Interest21Q)));
        figures.Add(Ui.Line("Capital works", Ui.Money(t.CapitalWorks21F)));
        figures.Add(Ui.Line("Other deductions", Ui.Money(t.OtherDeductions21U)));
        figures.Add(new Label { Text = "Your share, as on the tax return (item 21).", FontSize = 12, TextColor = Colors.Gray });

        figures.Add(Ui.Heading("PROPERTIES"));
        foreach (var p in properties.OrderByDescending(p => p.Active).ThenBy(p => p.Address))
        {
            var thisYear = schedule.Properties.FirstOrDefault(s => s.Id == p.PropertyId);
            var under = p.PayoutCode is { Length: > 0 } code ? code : "No payout code yet";
            if (p.OwnershipPct != 100)
                under += $"  ·  you own {p.OwnershipPct:0.##}%";
            if (!p.Active)
                under += "  ·  no longer rented";
            var net = thisYear?.NetRent ?? 0;
            var portfolioId = portfolio.PortfolioId;
            var canEdit = portfolio.CanEdit;
            figures.Add(Ui.Line(p.Address, Ui.Money(net), Ui.Shade(net), under)
                .OnTap(() => Navigation.PushAsync(new PropertyPage(Session, portfolioId, canEdit, p, chosen))));
        }
    }
}
