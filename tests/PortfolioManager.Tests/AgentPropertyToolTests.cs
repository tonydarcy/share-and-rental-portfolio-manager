using PortfolioManager.Core.Accounting;
using PortfolioManager.Web.Api;

namespace PortfolioManager.Tests;

public class AgentPropertyToolTests
{
    private static readonly AgentTools Tools = new();

    [Fact]
    public void The_property_tools_are_there_and_marked_as_writing_or_not()
    {
        Assert.False(Tools.Find("list_rental_properties")!.Writes);
        Assert.True(Tools.Find("save_rental_property")!.Writes);
        Assert.True(Tools.Find("allocate_bank_transaction_to_property")!.Writes);
    }

    [Fact]
    public void Allocating_to_a_property_needs_the_line_the_property_and_the_amounts()
    {
        var required = Tools.Find("allocate_bank_transaction_to_property")!.InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

        Assert.Contains("lineId", required);
        Assert.Contains("propertyCode", required);
        Assert.Contains("amounts", required);
    }

    [Fact]
    public void A_rental_entry_can_take_an_offset_account_for_non_cash_amounts()
    {
        var properties = Tools.Find("add_rental_entry")!.InputSchema["properties"]!.AsObject();

        Assert.True(properties.ContainsKey("offsetAccountId"));
    }

    [Theory]
    [InlineData(2000, 2000)]       // rent received
    [InlineData(1780.50, 1780.50)] // rent less agent fees
    [InlineData(-412.30, -412.30)] // an expense paid
    public void A_rental_entry_settles_a_bank_line_of_the_same_net(decimal line, decimal net) =>
        Assert.Null(BankImportService.RentalMismatch(line, net));

    [Fact]
    public void A_rental_entry_that_doesnt_add_up_to_the_deposit_is_refused() =>
        Assert.Contains("deposit is 2,000.00", BankImportService.RentalMismatch(2000, 1800));

    [Fact]
    public void A_withdrawal_is_compared_with_the_expenses() =>
        Assert.Contains("expenses less any rent come to 400.00, but the withdrawal is 412.30", BankImportService.RentalMismatch(-412.30m, -400));

    [Fact]
    public void A_deposit_cant_be_settled_by_expenses() =>
        Assert.NotNull(BankImportService.RentalMismatch(500, -500));
}
