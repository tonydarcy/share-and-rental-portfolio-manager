using PortfolioManager.Mobile.Client;

namespace PortfolioManager.Mobile.Pages;

/// <summary>One share: its latest price and change, units held, the year's range and a price chart.</summary>
public sealed class SharePage : ContentPage
{
    private static readonly (string Label, int Months)[] Spans = [("3M", 3), ("1Y", 12), ("5Y", 60)];
    private readonly Session session;
    private readonly int portfolioId;
    private readonly string exchange, code;
    private readonly Label name = new() { FontSize = 15, TextColor = Colors.Gray };
    private readonly Label price = new() { FontSize = 32, FontAttributes = FontAttributes.Bold };
    private readonly Label change = new() { FontSize = 15 };
    private readonly Label message = new() { IsVisible = false, Margin = new Thickness(0, 8) };
    private readonly PriceChart chart = new();
    private readonly GraphicsView chartView;
    private readonly HorizontalStackLayout spanButtons = new() { Spacing = 8, HorizontalOptions = LayoutOptions.Center };
    private readonly VerticalStackLayout details = [];
    private int months = 12, request;

    public SharePage(Session session, int portfolioId, string exchange, string code)
    {
        this.session = session;
        this.portfolioId = portfolioId;
        this.exchange = exchange;
        this.code = code;
        Title = code;
        chartView = new GraphicsView { Drawable = chart, HeightRequest = 220, Margin = new Thickness(0, 12) };
        foreach (var (label, m) in Spans)
            spanButtons.Add(new Button { Text = label, Padding = new Thickness(14, 4), Command = new Command(async () => { months = m; await LoadAsync(); }) });
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 8),
                Children = { name, price, change, message, chartView, spanButtons, details },
            },
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (chart.Points.Count == 0)
            await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var mine = ++request;
        message.IsVisible = false;
        try
        {
            var from = DateOnly.FromDateTime(DateTime.Today).AddMonths(-months);
            var api = session.Api;
            var h = await api.GetPriceHistoryAsync(portfolioId, exchange, code, from);
            if (mine != request)
                return;
            name.Text = h.Name is { Length: > 0 } n ? $"{n}  ·  {h.Exchange}" : h.Exchange;
            price.Text = h.Latest is { } p ? p.ToString("#,##0.00##") : "No price";
            change.Text = h.Change is { } c
                ? $"{c:+#,##0.00##;-#,##0.00##;0.00}" + (h.ChangePct is { } pct ? $" ({pct:+0.00%;-0.00%;0.00%})" : "")
                  + (h.LatestDate is { } d ? $" on {d:d MMM}" : "")
                : "";
            change.TextColor = Ui.Shade((decimal)(h.Change ?? 0));

            chart.Points = h.Prices.Select(x => (x.Date, x.Close)).ToList();
            chartView.Invalidate();

            details.Clear();
            details.Add(Ui.Heading("YOUR HOLDING"));
            var units = h.UnitsHeld ?? 0;
            details.Add(Ui.Line("Units", units.ToString("#,##0.####")));
            if (units != 0 && h.Latest is { } latest)
                details.Add(Ui.Line("Value", Ui.Money(units * (decimal)latest)));
            details.Add(Ui.Heading("PRICE"));
            if (h.YearLow is { } low && h.YearHigh is { } high)
                details.Add(Ui.Line("Year's range", $"{low:#,##0.00##} to {high:#,##0.00##}"));
            if (h.Sector is { Length: > 0 } sector)
                details.Add(Ui.Line("Sector", sector));
            if (api.OfflineCopySaved is { } saved)
                Show($"Offline copy from {saved.ToLocalTime():d MMM h:mm tt}.");
        }
        catch (SiteException e)
        {
            if (mine == request)
                Show(e.Message);
        }
    }

    private void Show(string text)
    {
        message.Text = text;
        message.IsVisible = true;
    }

    /// <summary>A plain line chart of closing prices, with the lowest and highest marked.</summary>
    private sealed class PriceChart : IDrawable
    {
        public List<(DateOnly Date, double Close)> Points { get; set; } = [];

        public void Draw(ICanvas canvas, RectF rect)
        {
            if (Points.Count < 2)
            {
                canvas.FontColor = Colors.Gray;
                canvas.DrawString("Not enough prices on file for a chart.", rect, HorizontalAlignment.Center, VerticalAlignment.Center);
                return;
            }
            var low = Points.Min(p => p.Close);
            var high = Points.Max(p => p.Close);
            var spread = high - low is > 0 and var s ? s : 1;
            const float top = 18, bottom = 18;
            var height = rect.Height - top - bottom;
            float X(int i) => rect.Left + rect.Width * i / (Points.Count - 1);
            float Y(double v) => rect.Top + top + (float)((high - v) / spread) * height;

            var path = new PathF(X(0), Y(Points[0].Close));
            for (var i = 1; i < Points.Count; i++)
                path.LineTo(X(i), Y(Points[i].Close));
            canvas.StrokeColor = Points[^1].Close >= Points[0].Close ? Colors.SeaGreen : Colors.Firebrick;
            canvas.StrokeSize = 2;
            canvas.DrawPath(path);

            canvas.FontColor = Colors.Gray;
            canvas.FontSize = 11;
            canvas.DrawString($"High {high:#,##0.00##}", rect.Left, rect.Top, rect.Width, top, HorizontalAlignment.Left, VerticalAlignment.Top);
            canvas.DrawString($"Low {low:#,##0.00##}", rect.Left, rect.Bottom - bottom, rect.Width, bottom, HorizontalAlignment.Left, VerticalAlignment.Bottom);
            canvas.DrawString($"{Points[0].Date:d MMM yy} to {Points[^1].Date:d MMM yy}", rect.Left, rect.Bottom - bottom, rect.Width, bottom,
                HorizontalAlignment.Right, VerticalAlignment.Bottom);
        }
    }
}
