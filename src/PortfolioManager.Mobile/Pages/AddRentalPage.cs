using PortfolioManager.Mobile.Client;

namespace PortfolioManager.Mobile.Pages;

/// <summary>
/// Records a rental property's rent and expenses, e.g. a month's agent statement: rent received, then any expenses taken
/// out or paid. The net goes through the bank account.
/// </summary>
public sealed class AddRentalPage : EntryForm
{
    private readonly List<RentalPropertySummary> properties;
    private readonly Picker property = new() { Title = "Property" };
    private readonly DatePicker date = new() { Date = DateTime.Today };
    private readonly Entry rent = Amount();
    private readonly Entry agentFees = Amount();
    private readonly VerticalStackLayout expenses = new() { Spacing = 6 };
    private readonly List<(Picker Kind, Entry Amount)> expenseRows = [];
    private readonly Label net = new() { FontSize = 15, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Entry comment = new() { Placeholder = "e.g. October statement" };
    private static readonly List<RentalAmount> OtherExpenses =
        RentalAmounts.Expenses.Where(a => a.Key != "propertyAgentFeesAndCommission").ToList();

    /// <summary><paramref name="choices"/>: the properties to pick from; only those with a payout code are offered.</summary>
    public AddRentalPage(Session session, int portfolioId, IReadOnlyList<RentalPropertySummary> choices) : base(session, portfolioId, "Add rental entry")
    {
        properties = choices.Where(p => p.PayoutCode is { Length: > 0 }).OrderByDescending(p => p.Active).ThenBy(p => p.Address).ToList();
        property.ItemsSource = properties.Select(p => p.Address).ToList();
        if (properties.Count == 1)
            property.SelectedIndex = 0;

        rent.TextChanged += (_, _) => ShowNet();
        agentFees.TextChanged += (_, _) => ShowNet();
        var more = new Button { Text = "Add another expense", Margin = new Thickness(0, 4) };
        more.Clicked += (_, _) => AddExpenseRow();

        Form.Add(Caption("Property"));
        Form.Add(property);
        Form.Add(Caption("Date"));
        Form.Add(date);
        Form.Add(Caption("Rent received (whole property)"));
        Form.Add(rent);
        Form.Add(Caption("Agent fees and commission"));
        Form.Add(agentFees);
        Form.Add(Caption("Other expenses"));
        Form.Add(expenses);
        Form.Add(more);
        Form.Add(net);
        Form.Add(Caption("Description"));
        Form.Add(comment);
        AddExpenseRow();
        ShowNet();
        Finish("Enter the whole property's amounts, positive; expenses are taken off the rent and the net goes through the bank "
            + "account. If this cash is on a bank statement you import, allocate that line to the property on the site instead, "
            + "so it isn't counted twice. Depreciation and other entries with no cash are on the site.");
    }

    private void AddExpenseRow()
    {
        var kind = new Picker { Title = "Expense", ItemsSource = OtherExpenses.Select(a => a.Label).ToList() };
        var amount = Amount();
        amount.TextChanged += (_, _) => ShowNet();
        kind.SelectedIndexChanged += (_, _) => ShowNet();
        var row = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(new GridLength(120)) },
        };
        row.Add(kind, 0, 0);
        row.Add(amount, 1, 0);
        expenses.Add(row);
        expenseRows.Add((kind, amount));
    }

    private Dictionary<string, decimal> Amounts()
    {
        var amounts = new Dictionary<string, decimal>();
        void Add(string key, decimal value)
        {
            if (value != 0)
                amounts[key] = amounts.GetValueOrDefault(key) + value;
        }
        Add("rentalIncome", Number(rent));
        Add("propertyAgentFeesAndCommission", Number(agentFees));
        foreach (var (kind, amount) in expenseRows)
            if (kind.SelectedIndex >= 0)
                Add(OtherExpenses[kind.SelectedIndex].Key, Number(amount));
        return amounts;
    }

    private void ShowNet() => net.Text = "Net to the bank: " + Ui.Signed(RentalAmounts.Net(Amounts()));

    private RentalRequest Request() => new(Day(date),
        property.SelectedIndex >= 0 ? properties[property.SelectedIndex].PayoutCode! : "",
        Amounts(), string.IsNullOrWhiteSpace(comment.Text) ? null : comment.Text.Trim());

    protected override IReadOnlyList<string> Check()
    {
        // Check each box before rows of the same kind are added together, so a typo or a minus can't slip through
        var boxes = new[] { rent, agentFees }.Concat(expenseRows.Select(r => r.Amount)).ToList();
        if (boxes.Any(b => !BlankOrNumber(b)))
            return ["One of the amounts isn't a number. Check the amounts."];
        if (boxes.Any(b => Number(b) < 0))
            return ["Enter amounts as positive numbers; expenses are taken off the rent."];
        var errors = EntryChecks.Rental(Request(), Today).ToList();
        if (expenseRows.Any(r => r.Kind.SelectedIndex < 0 && Number(r.Amount) != 0))
            errors.Add("Pick what each other expense was for.");
        return errors;
    }

    protected override async Task<string> SendAsync(SiteApi api) =>
        (await api.AddRentalAsync(PortfolioId, Request())).Message ?? "Saved.";
}
