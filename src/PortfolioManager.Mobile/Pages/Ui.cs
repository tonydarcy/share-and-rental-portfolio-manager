namespace PortfolioManager.Mobile.Pages;

/// <summary>Small helpers shared by the pages: money, percentages and gain colours.</summary>
internal static class Ui
{
    public static string Money(decimal amount) => amount.ToString("#,##0.00");

    public static string Signed(decimal amount) => amount.ToString("+#,##0.00;-#,##0.00;0.00");

    public static string Percent(double fraction) => fraction.ToString("+0.00%;-0.00%;0.00%");

    public static Color Shade(decimal amount) => amount < 0 ? Colors.Firebrick : amount > 0 ? Colors.SeaGreen : Colors.Gray;

    public static Label Heading(string text) => new()
    {
        Text = text, FontSize = 13, FontAttributes = FontAttributes.Bold, TextColor = Colors.Gray, Margin = new Thickness(0, 16, 0, 4),
    };

    /// <summary>A two-column line: words on the left, a figure on the right.</summary>
    public static Grid Line(string left, string right, Color? rightColour = null, string? under = null)
    {
        var grid = new Grid
        {
            Padding = new Thickness(0, 6),
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) },
        };
        grid.Add(new Label { Text = left, FontSize = 16 }, 0, 0);
        var figure = new Label { Text = right, FontSize = 16, HorizontalTextAlignment = TextAlignment.End };
        if (rightColour is not null)
            figure.TextColor = rightColour;
        grid.Add(figure, 1, 0);
        if (under is not null)
            grid.Add(new Label { Text = under, FontSize = 13, TextColor = Colors.Gray }, 0, 1);
        return grid;
    }

    /// <summary>Makes a view open something when tapped.</summary>
    public static T OnTap<T>(this T view, Func<Task> action) where T : View
    {
        view.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await action()) });
        return view;
    }
}
