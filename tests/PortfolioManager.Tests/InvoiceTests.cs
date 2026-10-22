using System.Xml.Linq;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.SalesTax;

namespace PortfolioManager.Tests;

public class InvoiceTests
{
    private static readonly TaxCode Gst = new(1, "GST", "GST on sales", TaxKind.Sales, 10m, "G1", "1A", true, true, 0);
    private static readonly Dictionary<int, TaxCode> Codes = new() { [1] = Gst };

    private static InvoiceInput Input(bool includesTax, params InvoiceLineInput[] lines) => new()
    {
        CustomerId = 5,
        IssueDate = new DateTime(2026, 10, 1),
        DueDate = new DateTime(2026, 10, 15),
        AmountsIncludeTax = includesTax,
        Lines = [.. lines],
    };

    private static InvoiceLineInput Line(string description, decimal qty, decimal price, int? taxCode = 1) =>
        new() { Description = description, Quantity = qty, UnitPrice = price, AccountId = 26, TaxCodeId = taxCode };

    [Fact]
    public void Tax_is_added_to_prices_that_exclude_it()
    {
        Assert.Equal((200m, 20m, 220m), InvoiceRules.Amounts(2, 100, Gst, amountsIncludeTax: false));
    }

    [Fact]
    public void Tax_is_taken_out_of_prices_that_include_it()
    {
        Assert.Equal((100m, 10m, 110m), InvoiceRules.Amounts(1, 110, Gst, amountsIncludeTax: true));
    }

    [Fact]
    public void A_line_without_a_tax_code_has_no_tax()
    {
        Assert.Equal((33.33m, 0m, 33.33m), InvoiceRules.Amounts(1, 33.333m, null, amountsIncludeTax: false));
    }

    [Fact]
    public void A_discount_line_has_negative_tax()
    {
        Assert.Equal((-10m, -1m, -11m), InvoiceRules.Amounts(1, -10, Gst, amountsIncludeTax: false));
    }

    [Fact]
    public void Totals_add_up_the_used_lines_and_ignore_blank_ones()
    {
        var input = Input(false, Line("Consulting", 2, 100), Line("Discount", 1, -10, null), new InvoiceLineInput());
        Assert.Equal((190m, 20m, 210m), input.Totals(Codes));
    }

    [Fact]
    public void A_valid_invoice_has_no_errors()
    {
        var input = Input(false, Line("Consulting", 2, 100));
        Assert.Empty(input.Validate(new HashSet<int> { 5 }, new HashSet<int> { 26 }, Codes));
    }

    [Fact]
    public void Validation_names_each_problem()
    {
        var input = Input(false, new InvoiceLineInput { Description = "No price", Quantity = 1, AccountId = 99, TaxCodeId = 7 });
        input.CustomerId = 0;
        input.DueDate = new DateTime(2026, 9, 1);
        var errors = input.Validate(new HashSet<int> { 5 }, new HashSet<int> { 26 }, Codes);
        Assert.Contains("Choose a customer.", errors);
        Assert.Contains("The due date can't be before the invoice date.", errors);
        Assert.Contains("Line 1: enter the price.", errors);
        Assert.Contains("Line 1: choose the account the income goes to.", errors);
        Assert.Contains("Line 1: choose a tax code from the list.", errors);
    }

    [Fact]
    public void An_invoice_must_total_more_than_zero()
    {
        var input = Input(false, Line("Credit", 1, -50));
        Assert.Contains("The invoice total must be more than zero.", input.Validate(new HashSet<int> { 5 }, new HashSet<int> { 26 }, Codes));
    }

    [Fact]
    public void Lines_go_to_the_database_as_xml_with_their_worked_out_amounts()
    {
        var input = Input(true, Line("Rent & outgoings", 1, 110), new InvoiceLineInput());
        var l = Assert.Single(XElement.Parse(input.LinesXml(Codes)).Elements("l"));
        Assert.Equal("Rent & outgoings", (string?)l.Attribute("d"));
        Assert.Equal("100", (string?)l.Attribute("n"));
        Assert.Equal("10", (string?)l.Attribute("x"));
        Assert.Equal("110", (string?)l.Attribute("g"));
        Assert.Equal("26", (string?)l.Attribute("a"));
        Assert.Equal("1", (string?)l.Attribute("t"));
    }

    [Fact]
    public void Save_parameters_carry_the_portfolio_and_the_approve_flag()
    {
        var input = Input(false, Line("Consulting", 1, 100));
        var p = InvoiceService.SaveParameters(42, 7, input, approve: true, Codes);
        object? Get(string name) => p.GetType().GetProperty(name)!.GetValue(p);
        Assert.Equal(42, Get("iUserId"));
        Assert.Equal(7, Get("iInvoiceID"));
        Assert.Equal(true, Get("bApprove"));
        Assert.Equal(new DateTime(2026, 10, 15), Get("dtDueDate"));
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft, 0, "2026-10-30", "Draft")]
    [InlineData(InvoiceStatus.Void, 0, "2026-10-30", "Void")]
    [InlineData(InvoiceStatus.Approved, 110, "2026-10-01", "Paid")]
    [InlineData(InvoiceStatus.Approved, 0, "2026-10-01", "Overdue")]
    [InlineData(InvoiceStatus.Approved, 50, "2026-10-30", "Part paid")]
    [InlineData(InvoiceStatus.Approved, 0, "2026-10-30", "Unpaid")]
    public void Status_shows_paid_overdue_and_part_paid(string status, decimal paid, string due, string expected) =>
        Assert.Equal(expected, InvoiceRules.DisplayStatus(status, 110, paid, DateOnly.Parse(due), new DateOnly(2026, 10, 18)));

    [Fact]
    public void Customer_needs_a_name_and_a_sensible_email()
    {
        Assert.Contains("Enter the customer's name.", new CustomerInput().Validate());
        Assert.Contains("Check the email address.", new CustomerInput { Name = "Acme", Email = "nope" }.Validate());
        Assert.Empty(new CustomerInput { Name = "Acme", Email = "jo@acme.com" }.Validate());
    }

    [Fact]
    public void Settings_need_a_positive_number_and_sensible_terms()
    {
        Assert.NotEmpty(new InvoiceSettingsInput { NextNumber = 0 }.Validate());
        Assert.NotEmpty(new InvoiceSettingsInput { DueDays = 400 }.Validate());
        Assert.Empty(new InvoiceSettingsInput { NumberPrefix = "INV-", NextNumber = 1, DueDays = 14 }.Validate());
    }

    [Fact]
    public void Invoice_rows_map_with_their_lines_and_payments()
    {
        var header = new Dictionary<string, object?>
        {
            ["iInvoiceID"] = 3, ["strInvoiceNumber"] = "INV-0003", ["iCustomerID"] = 5, ["dtmIssueDate"] = new DateTime(2026, 10, 1),
            ["dtmDueDate"] = new DateTime(2026, 10, 15), ["strStatus"] = "Approved", ["dblNet"] = 100m, ["dblTax"] = 10m, ["dblTotal"] = 110m,
            ["bAmountsIncludeTax"] = false, ["CustomerName"] = "Acme", ["strReference"] = DBNull.Value,
        };
        var line = new Dictionary<string, object?>
        {
            ["iInvoiceLineID"] = 1, ["strDescription"] = "Consulting", ["dblQuantity"] = 1m, ["dblUnitPrice"] = 100m, ["iAccountID"] = 26,
            ["iTaxCodeID"] = 1, ["TaxCode"] = "GST", ["dblNet"] = 100m, ["dblTax"] = 10m, ["dblTotal"] = 110m,
        };
        var payment = new Dictionary<string, object?>
        {
            ["iPaymentID"] = 9, ["dtmPaymentDate"] = new DateTime(2026, 10, 10), ["dblAmount"] = 60m, ["iAccountID"] = 6, ["strAccountName"] = "Cash at Bank",
        };

        var invoice = InvoiceService.MapDetail(header, [line], [payment])!;
        Assert.Equal("INV-0003", invoice.Number);
        Assert.Null(invoice.Reference);
        Assert.Equal("Acme", invoice.Customer.Name);
        Assert.Equal(1, Assert.Single(invoice.Lines).TaxCodeId);
        Assert.Equal(50m, invoice.Balance);
        Assert.Null(InvoiceService.MapDetail(null, [], []));
    }
}
