using PortfolioManager.Mobile.Client;

namespace PortfolioManager.Mobile.Pages;

/// <summary>Records a bank deposit or withdrawal (in the bank account used last).</summary>
public sealed class AddCashPage : EntryForm
{
    private readonly Picker direction = new() { ItemsSource = new List<string> { "Deposit", "Withdrawal" }, SelectedIndex = 0 };
    private readonly DatePicker date = new() { Date = DateTime.Today, MaximumDate = DateTime.Today };
    private readonly Entry amount = Amount();
    private readonly Entry comment = new() { Placeholder = "What it was for" };

    public AddCashPage(Session session, int portfolioId) : base(session, portfolioId, "Add cash")
    {
        Form.Add(Caption("Deposit or withdrawal"));
        Form.Add(direction);
        Form.Add(Caption("Date"));
        Form.Add(date);
        Form.Add(Caption("Amount"));
        Form.Add(amount);
        Form.Add(Caption("Description"));
        Form.Add(comment);
        Finish("Goes into the bank account used last. In double-entry books it's posted against Owner's Capital; "
            + "to code it to another account, use the site.");
    }

    private CashRequest Request() => new(Day(date), (string)direction.SelectedItem, Number(amount),
        string.IsNullOrWhiteSpace(comment.Text) ? null : comment.Text.Trim());

    protected override IReadOnlyList<string> Check() => EntryChecks.Cash(Request());

    protected override async Task<string> SendAsync(SiteApi api) =>
        (await api.AddCashAsync(PortfolioId, Request())).Message ?? "Saved.";
}
