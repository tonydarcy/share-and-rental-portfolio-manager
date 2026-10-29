using System.Text.Json;
using PortfolioManager.Core.Api;
using PortfolioManager.Core.Cash;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Web.Api;

namespace PortfolioManager.Tests;

public class AgentBooksToolTests
{
    private static readonly ApiCaller Editor = new(7, 42, "Claude", ReadOnly: false, "tony@example.com");
    private static readonly AgentTools Tools = new();

    private static AgentCall Call(object args) => new(Editor, null, JsonSerializer.SerializeToElement(args), null!);

    [Fact]
    public void An_edit_keeps_everything_not_sent()
    {
        var stored = new CashForm { Date = new DateOnly(2026, 1, 5), Direction = "Deposit", Amount = 100, BankAccount = "Cash", Comment = "Opening" };

        var merged = Call(new { cashTransactionId = 9, amount = 250, comment = (string?)null }).BindOver(stored);

        Assert.Equal(250m, merged.Amount);
        Assert.Equal(new DateOnly(2026, 1, 5), merged.Date);
        Assert.Equal("Deposit", merged.Direction);
        Assert.Equal("Cash", merged.BankAccount);
        // Null means "keep it", as with leaving it out
        Assert.Equal("Opening", merged.Comment);
    }

    [Fact]
    public void An_edit_explains_a_badly_formatted_argument()
    {
        var ex = Assert.Throws<AgentToolException>(() => Call(new { date = "5 Jan 2026" }).BindOver(new CashForm()));
        Assert.Equal(400, ex.Status);
        Assert.Contains("yyyy-mm-dd", ex.Message);
    }

    [Fact]
    public void A_stored_cash_entry_fills_the_add_cash_form()
    {
        var form = CashService.ToForm(new CashTransaction(3, "Cash", new DateTime(2026, 2, 1), "Fees", 12.5m, 0, 0, "AUD", null));

        Assert.Equal("Withdrawal", form.Direction);
        Assert.Equal(12.5m, form.Amount);
        Assert.Equal(new DateOnly(2026, 2, 1), form.Date);
        Assert.Equal("Fees", form.Comment);
    }

    [Fact]
    public void Rental_amounts_have_short_names()
    {
        var keys = RentalPayoutInput.Fields.Select(RentalPayoutInput.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Contains("rentalIncome", keys);
        Assert.Contains("bodyCorporateFees", keys);
        Assert.Contains("councilRates", keys);
        Assert.Contains("borrowingExpenses", keys);
        Assert.Contains("sundryExpense10", keys);
    }

    [Fact]
    public void Rental_amounts_are_set_by_name_and_unknown_names_are_reported()
    {
        var input = new RentalPayoutInput();
        var unknown = input.SetAmounts(new Dictionary<string, decimal> { ["RentalIncome"] = 2400, ["councilRates"] = 300, ["rent"] = 1 });

        Assert.Equal(["rent"], unknown);
        Assert.Equal(2100m, input.Net);
        Assert.Equal(new Dictionary<string, decimal> { ["rentalIncome"] = 2400, ["councilRates"] = 300 }, input.NamedAmounts());
    }

    [Fact]
    public void A_drp_payout_keeps_its_code_and_amounts()
    {
        var record = new PayoutRecord { StockExchangeCode = "ASX", StockCode = "BHP", Date = new DateTime(2026, 9, 30), Total = 100, Net = 100, Franked = 100, Confirmed = "Yes", ReinvestmentTradeId = 5 };
        var input = PayoutEdit.FromRecord(record);
        input.StockCode = "CBA";
        input.Total = 500;
        input.Franked = 500;
        input.Comment = "Changed";

        var (keepAmounts, errors) = PayoutEdit.Prepare(record, input);

        Assert.True(keepAmounts);
        Assert.Empty(errors);
        Assert.Equal("BHP", input.StockCode);
        Assert.Equal(100m, input.Total);
        Assert.Equal("Changed", input.Comment);
    }

    [Fact]
    public void Changed_payout_amounts_must_add_up()
    {
        var record = new PayoutRecord { StockExchangeCode = "ASX", StockCode = "BHP", Date = new DateTime(2026, 9, 30), Total = 100, Net = 100, Franked = 100, Confirmed = "Yes" };
        var input = PayoutEdit.FromRecord(record);
        input.Total = 120;

        var (keepAmounts, errors) = PayoutEdit.Prepare(record, input);

        Assert.False(keepAmounts);
        Assert.Contains(errors, e => e.Contains("cash received"));
    }

    [Fact]
    public void Every_new_change_tool_needs_edit_access_and_reads_dont()
    {
        string[] writers =
        [
            "edit_payout", "add_drp", "add_rental_entry", "edit_rental_entry", "edit_cash_transaction", "delete_cash_transaction",
            "add_split", "delete_split", "add_return_of_capital", "delete_return_of_capital", "save_journal", "delete_journal",
            "save_customer", "save_invoice", "send_invoice", "add_invoice_payment", "save_supplier", "save_bill", "add_bill_payment",
            "set_manual_price", "delete_manual_price",
        ];
        Assert.All(writers, name => Assert.True(Tools.Find(name)!.Writes, name));
        Assert.All(new[] { "list_corporate_actions", "list_journals", "get_journal", "list_customers", "list_suppliers", "list_bills", "get_bill", "list_manual_prices" },
            name => Assert.False(Tools.Find(name)!.Writes, name));
    }

    [Fact]
    public void Rental_tools_list_every_amount_name_in_their_schema()
    {
        var amounts = Tools.Find("add_rental_entry")!.InputSchema["properties"]!["amounts"]!["properties"]!.AsObject();
        Assert.Equal(RentalPayoutInput.Fields.Count, amounts.Count);
        Assert.Equal("number", amounts["rentalIncome"]!["type"]!.GetValue<string>());
    }
}
