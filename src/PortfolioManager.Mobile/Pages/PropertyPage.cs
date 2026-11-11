using PortfolioManager.Mobile.Client;

namespace PortfolioManager.Mobile.Pages;

/// <summary>One rental property: its schedule for the year (whole property and your share) and the year's entries.</summary>
public sealed class PropertyPage : ContentPage
{
    private static readonly List<string> Years = ["This financial year", "Last financial year"];
    private readonly Session session;
    private readonly int portfolioId;
    private readonly RentalPropertySummary property;
    private readonly Picker year = new() { Title = "Year", ItemsSource = Years };
    private readonly Label message = new() { IsVisible = false, Margin = new Thickness(0, 8) };
    private readonly VerticalStackLayout figures = [];
    private int request, shownVersion = -1;

    public PropertyPage(Session session, int portfolioId, bool canEdit, RentalPropertySummary property, int yearsBack)
    {
        this.session = session;
        this.portfolioId = portfolioId;
        this.property = property;
        Title = property.PayoutCode is { Length: > 0 } code ? code : "Property";
        year.SelectedIndex = yearsBack;
        year.SelectedIndexChanged += async (_, _) => await LoadAsync();

        var about = new VerticalStackLayout { new Label { Text = property.Address, FontSize = 20, FontAttributes = FontAttributes.Bold } };
        var facts = new List<string>();
        if (property.OwnershipPct != 100)
            facts.Add($"You own {property.OwnershipPct:0.##}%");
        if (property.FirstEarned is { } first)
            facts.Add($"rented since {first:d MMM yyyy}");
        if (property.Value is { } value)
            facts.Add($"valued at {Ui.Money(value)}" + (property.Valued is { } on ? $" on {on:d MMM yyyy}" : ""));
        if (facts.Count > 0)
            about.Add(new Label { Text = string.Join(", ", facts) + ".", FontSize = 13, TextColor = Colors.Gray });
        if (property.PayoutCode is not { Length: > 0 })
            about.Add(new Label
            {
                Text = "This property has no payout code, so entries can't reach its schedule. Set one on the site (Property > Rental properties).",
                FontSize = 13, TextColor = Colors.Firebrick, Margin = new Thickness(0, 6),
            });

        if (canEdit && property.PayoutCode is { Length: > 0 })
            ToolbarItems.Add(new ToolbarItem("Add", null, async () =>
                await Navigation.PushAsync(new AddRentalPage(session, portfolioId, [property]))));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout { Padding = new Thickness(16, 8), Children = { about, year, message, figures } },
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // First visit, or an entry was saved
        if (shownVersion != session.Version)
            await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var mine = ++request;
        message.IsVisible = false;
        var chosen = year.SelectedIndex;
        try
        {
            var api = session.Api;
            var version = session.Version;
            var yearEnding = ReportPeriod.YearEnding(DateOnly.FromDateTime(DateTime.Today), chosen);
            var schedule = await api.GetRentalScheduleAsync(portfolioId, yearEnding);
            PayoutList? entries = null;
            if (property.PayoutCode is { Length: > 0 } code)
                entries = await api.ListPayoutsAsync(portfolioId, new DateOnly(yearEnding - 1, 7, 1), new DateOnly(yearEnding, 6, 30), 100, stockCode: code);
            if (mine != request)
                return;
            shownVersion = version;
            Show(schedule.Properties.FirstOrDefault(p => p.Id == property.PropertyId), entries,
                schedule.FinancialYear ?? $"year to 30 June {yearEnding}");
            if (api.OfflineCopySaved is { } saved)
                Problem($"Offline copy from {saved.ToLocalTime():d MMM h:mm tt}. Go back and open it again to retry.");
        }
        catch (SiteException e) when (mine == request)
        {
            figures.Clear();
            if (e.SignedOut)
            {
                await DisplayAlertAsync("Signed out", e.Message, "OK");
                session.SignOut();
                ((App)Application.Current!).ShowStart();
                return;
            }
            Problem(e.Message);
        }
        catch (SiteException)
        {
            // From a year no longer picked
        }
    }

    private void Show(RentalPropertyYear? p, PayoutList? entries, string yearName)
    {
        figures.Clear();
        figures.Add(new Label { Text = "Net rent, your share, " + yearName, FontSize = 13, TextColor = Colors.Gray, Margin = new Thickness(0, 8, 0, 0) });
        var net = p?.NetRent ?? 0;
        figures.Add(new Label { Text = Ui.Money(net), FontSize = 28, FontAttributes = FontAttributes.Bold, TextColor = Ui.Shade(net) });

        figures.Add(Ui.Heading("SCHEDULE (WHOLE PROPERTY)"));
        var lines = p?.Lines ?? [];
        if (lines.Count == 0)
            figures.Add(new Label { Text = "Nothing recorded for this year.", TextColor = Colors.Gray });
        foreach (var l in lines)
            figures.Add(Ui.Line(l.Name, Ui.Money(l.WholeProperty), null, l.OwnersShare != l.WholeProperty ? $"Your share {Ui.Money(l.OwnersShare)}" : null));
        if (p is { ExpensesClaimedPct: < 100 })
            figures.Add(new Label { Text = $"Only {p.ExpensesClaimedPct:0.##}% of expenses are claimed (private use).", FontSize = 12, TextColor = Colors.Gray });

        if (entries is null)
            return;
        figures.Add(Ui.Heading("ENTRIES"));
        if (entries.Payouts.Rows.Count == 0)
            figures.Add(new Label { Text = "No entries this year.", TextColor = Colors.Gray });
        foreach (var e in entries.Payouts.Rows)
        {
            var detail = e.Date is { } d ? d.ToString("d MMM yyyy") : "";
            if (e.RentalIncome != 0 || e.RentalExpenses != 0)
                detail += $"  ·  rent {Ui.Money(e.RentalIncome)}, expenses {Ui.Money(e.RentalExpenses)}";
            if (!string.IsNullOrWhiteSpace(e.Comment))
                detail += "\n" + e.Comment.Trim();
            figures.Add(Ui.Line("Entry", Ui.Signed(e.Total), Ui.Shade(e.Total), detail));
        }
        if (entries.Payouts.Returned < entries.Payouts.Total)
            figures.Add(new Label { Text = $"Showing the newest {entries.Payouts.Returned} of {entries.Payouts.Total}. See the site for the rest.", FontSize = 12, TextColor = Colors.Gray });
        figures.Add(new Label
        {
            Text = "Depreciation worked out from the property's assets is in the schedule but not in these entries.",
            FontSize = 12, TextColor = Colors.Gray, Margin = new Thickness(0, 8),
        });
    }

    private void Problem(string text)
    {
        message.Text = text;
        message.IsVisible = true;
    }
}
