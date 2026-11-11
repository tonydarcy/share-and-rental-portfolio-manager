using PortfolioManager.Mobile.Client;

namespace PortfolioManager.Mobile.Pages;

/// <summary>The picked portfolio's holdings with today's value and gain. Pull down to refresh.</summary>
public sealed class HoldingsPage : ContentPage
{
    private readonly Session session;
    private readonly Picker portfolios = new() { Title = "Portfolio" };
    private readonly Label total = new() { FontSize = 28, FontAttributes = FontAttributes.Bold };
    private readonly Label gain = new() { FontSize = 15 };
    private readonly Label message = new() { IsVisible = false, Margin = new Thickness(0, 8) };
    private readonly CollectionView list = new() { SelectionMode = SelectionMode.None };
    private readonly RefreshView refresh = new();
    private List<PortfolioSummary> loaded = [];
    private bool busy;

    public HoldingsPage(Session session)
    {
        this.session = session;
        Title = "Holdings";
        ToolbarItems.Add(new ToolbarItem("Sign out", null, async () => await SignOutAsync()));

        portfolios.SelectedIndexChanged += async (_, _) =>
        {
            if (portfolios.SelectedIndex >= 0 && !busy)
            {
                session.LastPortfolioId = loaded[portfolios.SelectedIndex].PortfolioId;
                await LoadHoldingsAsync();
            }
        };

        list.ItemTemplate = new DataTemplate(Row);
        list.EmptyView = new Label { Text = "No holdings yet.", Margin = 16 };
        list.Header = new VerticalStackLayout
        {
            Padding = new Thickness(16, 8),
            Children = { portfolios, total, gain, message },
        };
        refresh.Content = list;
        refresh.Command = new Command(async () => await LoadAsync());
        Content = refresh;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (loaded.Count == 0)
        {
            refresh.IsRefreshing = true;
            await LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            if (!await session.LoadAsync())
            {
                await SignOutAsync();
                return;
            }
            busy = true;
            loaded = await session.Api.ListPortfoliosAsync();
            portfolios.ItemsSource = loaded.Select(p => p.Name).ToList();
            portfolios.IsVisible = loaded.Count > 1;
            var pick = loaded.FindIndex(p => p.PortfolioId == session.LastPortfolioId);
            portfolios.SelectedIndex = loaded.Count == 0 ? -1 : Math.Max(pick, 0);
            busy = false;
            if (loaded.Count == 0)
            {
                Show("This login has no portfolios yet. Add one on the site.");
                list.ItemsSource = null;
                return;
            }
            session.LastPortfolioId = loaded[portfolios.SelectedIndex].PortfolioId;
            await LoadHoldingsAsync();
        }
        catch (SiteException e)
        {
            await Problem(e);
        }
        finally
        {
            busy = false;
            refresh.IsRefreshing = false;
        }
    }

    private async Task LoadHoldingsAsync()
    {
        message.IsVisible = false;
        try
        {
            var picked = loaded[portfolios.SelectedIndex];
            Title = picked.Name;
            var holdings = await session.Api.GetHoldingsAsync(picked.PortfolioId);
            total.Text = Money(holdings.Total.MarketValue);
            gain.Text = $"Gain {Money(holdings.Total.CapitalGain)}  ·  Payouts {Money(holdings.Total.Payouts)}";
            gain.TextColor = Shade(holdings.Total.CapitalGain);
            list.ItemsSource = holdings.Holdings.OrderByDescending(h => h.MarketValue).ToList();
        }
        catch (SiteException e)
        {
            await Problem(e);
        }
    }

    private static View Row()
    {
        var code = new Label { FontAttributes = FontAttributes.Bold, FontSize = 16 };
        code.SetBinding(Label.TextProperty, nameof(Holding.StockCode));
        var units = new Label { FontSize = 13, TextColor = Colors.Gray };
        units.SetBinding(Label.TextProperty, new Binding(nameof(Holding.Units), stringFormat: "{0:#,##0.####} units"));
        var value = new Label { FontSize = 16, HorizontalTextAlignment = TextAlignment.End };
        value.SetBinding(Label.TextProperty, new Binding(nameof(Holding.MarketValue), stringFormat: "{0:#,##0.00}"));
        var change = new Label { FontSize = 13, HorizontalTextAlignment = TextAlignment.End };
        change.SetBinding(Label.TextProperty, new Binding(nameof(Holding.CapitalGain), stringFormat: "{0:+#,##0.00;-#,##0.00;0.00}"));
        change.SetBinding(Label.TextColorProperty, new Binding(nameof(Holding.CapitalGain), converter: new GainColour()));

        var grid = new Grid
        {
            Padding = new Thickness(16, 10),
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) },
        };
        grid.Add(code, 0, 0);
        grid.Add(units, 0, 1);
        grid.Add(value, 1, 0);
        grid.Add(change, 1, 1);
        return grid;
    }

    private async Task Problem(SiteException e)
    {
        if (e.SignedOut)
        {
            await DisplayAlertAsync("Signed out", e.Message, "OK");
            await SignOutAsync();
            return;
        }
        Show(e.Message);
    }

    private void Show(string text)
    {
        message.Text = text;
        message.IsVisible = true;
    }

    private async Task SignOutAsync()
    {
        session.SignOut();
        ((App)Application.Current!).ShowStart();
        await Task.CompletedTask;
    }

    private static string Money(decimal amount) => amount.ToString("#,##0.00");

    private static Color Shade(decimal amount) => amount < 0 ? Colors.Firebrick : amount > 0 ? Colors.SeaGreen : Colors.Gray;

    private sealed class GainColour : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            Shade(value is decimal d ? d : 0);

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
