using PortfolioManager.Core.Accounts;
using PortfolioManager.Core.Portfolios;

namespace PortfolioManager.Tests;

public class AccountValidationTests
{
    [Fact]
    public void A_good_registration_passes() =>
        Assert.Empty(AccountService.ValidateRegistration(" tony@example.com ", "correct horse", "correct horse"));

    [Theory]
    [InlineData("", "longenough1", "longenough1")]
    [InlineData("tony", "longenough1", "longenough1")]
    [InlineData("tony@example", "longenough1", "longenough1")]
    [InlineData("@example.com", "longenough1", "longenough1")]
    [InlineData("to ny@example.com", "longenough1", "longenough1")]
    [InlineData("tony@example.com", "short", "short")]
    [InlineData("tony@example.com", "123456789", "123456789")]
    [InlineData("tony@example.com", "longenough1", "longenough2")]
    public void Bad_registrations_are_rejected(string email, string password, string confirm) =>
        Assert.NotEmpty(AccountService.ValidateRegistration(email, password, confirm));

    [Fact]
    public void Ten_characters_is_enough() =>
        Assert.Empty(AccountService.ValidateRegistration("a@b.co", "1234567890", "1234567890"));

    [Theory]
    [InlineData("Super fund", "Superfund", true)]
    [InlineData("", "Individual", false)]
    [InlineData("Kids", "Partnership", false)]
    public void New_portfolios_need_a_name_and_a_known_type(string name, string type, bool ok) =>
        Assert.Equal(ok, PortfolioService.ValidateNew(name, type).Count == 0);
}
