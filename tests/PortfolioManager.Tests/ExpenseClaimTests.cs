using PortfolioManager.Core.Claims;
using PortfolioManager.Core.SalesTax;

namespace PortfolioManager.Tests;

public class ExpenseClaimTests
{
    private static readonly Dictionary<int, TaxCode> Codes = new() { [5] = new(5, "GST", "GST on purchases", TaxKind.Purchases, 10, null, null, true, true, 1) };

    private static ExpenseClaim Claim(int id, string claimant, decimal amount, string status = ClaimStatus.Approved) =>
        new(id, null, claimant, new DateOnly(2026, 10, 1), "Cafe", "Lunch", 60, "Meals", null, amount, 0, null, null, status, null, null, null, null, null, DateTime.Now);

    [Fact]
    public void Tax_is_worked_out_from_the_code_unless_typed()
    {
        Assert.Equal(10m, new ClaimInput { Amount = 110, TaxCodeId = 5 }.TaxFor(Codes));
        Assert.Equal(9m, new ClaimInput { Amount = 110, TaxCodeId = 5, Tax = 9 }.TaxFor(Codes));
        Assert.Equal(0m, new ClaimInput { Amount = 110, Tax = 9 }.TaxFor(Codes));
    }

    [Fact]
    public void Validate_needs_the_basics()
    {
        var errors = new ClaimInput { Amount = 0, AccountId = 99 }.Validate(new HashSet<int> { 60 }, Codes);
        Assert.Equal(5, errors.Count);
        Assert.Empty(new ClaimInput { Claimant = "Sam", Date = new DateTime(2026, 10, 1), Description = "Taxi", AccountId = 60, Amount = 25 }
            .Validate(new HashSet<int> { 60 }, Codes));
    }

    [Fact]
    public void Tax_more_than_the_amount_is_refused()
    {
        var input = new ClaimInput { Claimant = "Sam", Date = new DateTime(2026, 10, 1), Description = "Taxi", AccountId = 60, Amount = 25, TaxCodeId = 5, Tax = 30 };
        Assert.Contains("The tax can't be more than the amount.", input.Validate(new HashSet<int> { 60 }, Codes));
    }

    [Fact]
    public void Owing_groups_approved_claims_by_person()
    {
        var owing = ClaimRules.Owing([Claim(1, "Sam", 20), Claim(2, "sam ", 30), Claim(3, "Alex", 15), Claim(4, "Alex", 99, ClaimStatus.Submitted)]);
        Assert.Equal([("Alex", 1, 15m), ("Sam", 2, 50m)], owing.Select(o => (o.Claimant.Trim(), o.Count, o.Amount)));
    }

    [Fact]
    public void Claims_xml_lists_each_claim_once() =>
        Assert.Equal("<claims><c id=\"1\" /><c id=\"2\" /></claims>", ClaimRules.ClaimsXml([1, 2, 1]));
}
