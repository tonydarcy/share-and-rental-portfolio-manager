using PortfolioManager.Mobile.Client;

namespace PortfolioManager.Mobile.Pages;

/// <summary>The picked portfolio's holdings with today's value and gain. Tap one for its price chart.</summary>
public sealed class HoldingsPage : PortfolioPage
{
    private readonly Label total = new() { FontSize = 28, FontAttributes = FontAttributes.Bold };
    private readonly Label gain = new() { FontSize = 15 };
    private readonly CollectionView list = new() { SelectionMode = SelectionMode.Single };

    public HoldingsPage(Session session) : base(session, "Holdings")
    {
        list.ItemTemplate = new DataTemplate(Row);
        list.EmptyView = new Label { Text = "No holdings yet.", Margin = 16 };
        list.Header = new VerticalStackLayout { Padding = new Thickness(16, 8), Children = { Header, total, gain } };
        list.SelectionChanged += async (_, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is Holding h)
            {
                list.SelectedItem = null;
                await OpenShareAsync(h.ExchangeCode, h.StockCode);
            }
        };
        Refresh.Content = list;
    }

    protected override async Task ShowAsync(SiteApi api, PortfolioSummary portfolio, Func<bool> stillWanted)
    {
        var holdings = await api.GetHoldingsAsync(portfolio.PortfolioId);
        if (!stillWanted())
            return;
        total.Text = Ui.Money(holdings.Total.MarketValue);
        gain.Text = $"Gain {Ui.Money(holdings.Total.CapitalGain)}  ·  Payouts {Ui.Money(holdings.Total.Payouts)}";
        gain.TextColor = Ui.Shade(holdings.Total.CapitalGain);
        list.ItemsSource = holdings.Holdings.OrderByDescending(h => h.MarketValue).ToList();
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

    private sealed class GainColour : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            Ui.Shade(value is decimal d ? d : 0);

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
