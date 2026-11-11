using PortfolioManager.Mobile.Client;

namespace PortfolioManager.Mobile.Pages;

/// <summary>Records a dividend or distribution from a statement: cash received, the franked part and the franking credit.</summary>
public sealed class AddPayoutPage : EntryForm
{
    private readonly Picker holding = new() { Title = "Pick a holding" };
    private readonly List<Holding> holdings;
    private readonly DatePicker date = new() { Date = DateTime.Today };
    private readonly Entry cash = Amount();
    private readonly Entry franked = Amount();
    private readonly Entry credit = Amount();
    private readonly Entry comment = new() { Placeholder = "Optional" };
    private readonly Label unfranked = new() { FontSize = 13, TextColor = Colors.Gray };

    public AddPayoutPage(Session session, int portfolioId, IReadOnlyList<Holding> holdings, string? start = null)
        : base(session, portfolioId, "Add payout")
    {
        this.holdings = holdings.OrderBy(h => h.StockCode).ToList();
        holding.ItemsSource = this.holdings.Select(h => $"{h.StockCode} ({h.ExchangeCode})" + (h.Units > 0 ? "" : " sold")).ToList();
        holding.SelectedIndex = start is null ? -1 : this.holdings.FindIndex(h => h.StockCode == start);
        cash.TextChanged += (_, _) => ShowUnfranked();
        franked.TextChanged += (_, _) => ShowUnfranked();

        Form.Add(Caption("Share"));
        Form.Add(holding);
        Form.Add(Caption("Payment date"));
        Form.Add(date);
        Form.Add(Caption("Cash received"));
        Form.Add(cash);
        Form.Add(Caption("Franked amount"));
        Form.Add(franked);
        Form.Add(unfranked);
        Form.Add(Caption("Franking credit"));
        Form.Add(credit);
        Form.Add(new HorizontalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Button { Text = "Work out at 30%", Command = new Command(() => credit.Text = EntryChecks.FrankingCredit(Number(franked), 0.30m).ToString("0.00")) },
                new Button { Text = "At 25%", Command = new Command(() => credit.Text = EntryChecks.FrankingCredit(Number(franked), 0.25m).ToString("0.00")) },
            },
        });
        Form.Add(Caption("Note"));
        Form.Add(comment);
        Finish("For payouts with interest, trust income, foreign income or tax withheld, use the site's Add payout page. "
            + "Check the franking credit against the statement.");
    }

    private PayoutRequest Request()
    {
        var picked = holding.SelectedIndex >= 0 ? holdings[holding.SelectedIndex] : null;
        var total = Number(cash);
        var f = Number(franked);
        return new PayoutRequest(Day(date), picked?.ExchangeCode ?? "", picked?.StockCode ?? "", total, f, total - f, Number(credit),
            string.IsNullOrWhiteSpace(comment.Text) ? null : comment.Text.Trim());
    }

    private void ShowUnfranked()
    {
        var r = Request();
        unfranked.Text = r.Total > 0 ? $"Unfranked: {Ui.Money(r.Unfranked)}" : "";
    }

    protected override IReadOnlyList<string> Check() => EntryChecks.Payout(Request(), Today);

    protected override async Task<string> SendAsync(SiteApi api) =>
        (await api.AddPayoutAsync(PortfolioId, Request())).Message ?? "Payout saved.";
}
