using PortfolioManager.Core.Clients;
using PortfolioManager.Core.TaxWork;

namespace PortfolioManager.Tests;

public class PaperReturnTests
{
    [Theory]
    [InlineData("Individual", "NAT 2541")]
    [InlineData("Company", "NAT 0656")]
    [InlineData("Trust", "NAT 0660")]
    [InlineData("Partnership", "NAT 0659")]
    [InlineData("SMSF", "NAT 71226")]
    public void Each_entity_type_orders_its_own_form(string type, string nat) =>
        Assert.Equal(nat, PaperReturn.FormFor(type).Nat);

    [Theory]
    [InlineData("10L", PaperReturn.MainPart)]
    [InlineData("11U", PaperReturn.MainPart)]
    [InlineData("12A", PaperReturn.MainPart)]
    [InlineData("D7", PaperReturn.MainPart)]
    [InlineData("13U", PaperReturn.SupplementaryPart)]
    [InlineData("18H", PaperReturn.SupplementaryPart)]
    [InlineData("20M", PaperReturn.SupplementaryPart)]
    [InlineData("21P", PaperReturn.SupplementaryPart)]
    [InlineData("D12", PaperReturn.SupplementaryPart)]
    [InlineData("P8", PaperReturn.BusinessSchedulePart)]
    [InlineData("", PaperReturn.MainPart)]
    public void An_individuals_label_goes_on_the_right_part_of_the_form(string code, string part) =>
        Assert.Equal(part, PaperReturn.PartFor(ClientEntityTypes.Individual, code));

    [Fact]
    public void Other_entities_have_one_form() =>
        Assert.Equal(PaperReturn.MainPart, PaperReturn.PartFor(ClientEntityTypes.Company, "13A"));

    [Fact]
    public void Labels_are_grouped_main_return_first_then_by_section()
    {
        var s = new TaxReturnSchedule("Individual tax return", ClientEntityTypes.Individual, "Jo Smith", null, 2026,
        [
            new("Income", "10L", "Gross interest", 100, ""),
            new("Capital gains", "18H", "Total current year capital gains", 2_000, ""),
            new("Income", "11T", "Franked amount", 700, ""),
            new("Deductions", "D8", "Dividend deductions", 60, ""),
        ]);

        var parts = PaperReturn.Parts(s);

        Assert.Equal([PaperReturn.MainPart, PaperReturn.SupplementaryPart], parts.Select(p => p.Name));
        Assert.Equal(["Income", "Deductions"], parts[0].Sections.Select(x => x.Section));
        Assert.Equal(["10L", "11T"], parts[0].Sections[0].Labels.Select(l => l.Code));
    }

    [Theory]
    [InlineData("VIC", "MELBOURNE VIC 3001")]
    [InlineData(" qld ", "BRISBANE QLD 4001")]
    [InlineData("NT", "DARWIN NT 0801")]
    [InlineData(null, "SYDNEY NSW 2001")]
    [InlineData("Overseas", "SYDNEY NSW 2001")]
    public void The_return_is_posted_to_the_clients_capital_city(string? state, string lastLine)
    {
        var a = PaperReturn.AddressFor(state);
        Assert.Equal(["Australian Taxation Office", "GPO Box 9845", lastLine], a.Lines);
    }

    [Fact]
    public void A_loss_is_written_without_a_sign() => Assert.Equal("1,234.50", PaperReturn.FormAmount(-1234.5m));
}
