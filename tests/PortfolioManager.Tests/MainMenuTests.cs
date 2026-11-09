using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Tests;

/// <summary>The top menu: which parts show for which portfolio, the saved choices, and that every link goes somewhere.</summary>
public class MainMenuTests
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static MenuUsage Plain(bool australia = true, string entity = "Individual", int portfolios = 1) =>
        new(false, false, false, entity, null, null, portfolios, australia);

    private static IEnumerable<MenuItem> Items(IEnumerable<MenuSection> sections) =>
        sections.SelectMany(s => s.Groups).SelectMany(g => g.Items);

    private static IReadOnlyList<MenuSection> Menu(MenuUsage usage, bool admin = false) =>
        MainMenu.For(new MenuContext(usage, admin, "GST"));

    [Fact]
    public void Every_menu_link_is_a_page()
    {
        var everything = new MenuUsage(true, true, true, "Superfund", null, "Practice=on", 5, true);
        foreach (var context in new[] { new MenuContext(everything, true, "GST"), new MenuContext(everything with { IsAustralia = false, LoginChoices = "Practice=off" }, true, "VAT") })
        {
            foreach (var item in Items(MainMenu.All(context)))
            {
                var file = Path.Combine(Repo(), "src", "PortfolioManager.Web", "Pages", item.Page.TrimStart('/') + ".cshtml");
                var index = Path.Combine(Repo(), "src", "PortfolioManager.Web", "Pages", item.Page.TrimStart('/'), "Index.cshtml");
                Assert.True(File.Exists(file) || File.Exists(index), $"{item.Label} links to {item.Page}, which has no page");
            }
        }
    }

    [Fact]
    public void A_plain_share_investor_sees_a_short_menu()
    {
        var keys = Menu(Plain()).Select(s => s.Key).ToList();
        Assert.Equal(["investments", "add", "reports", "accounting", "tax"], keys);
        Assert.DoesNotContain(Items(Menu(Plain())), i => i.Page == "/Payouts/AddRental" || i.Page == "/TaxWork/Smsf");
    }

    [Fact]
    public void Business_and_property_show_once_used()
    {
        var keys = Menu(Plain() with { HasBusiness = true, HasProperty = true }).Select(s => s.Key).ToList();
        Assert.Contains("business", keys);
        Assert.Contains("property", keys);
        Assert.Contains(Items(Menu(Plain() with { HasProperty = true })), i => i.Page == "/Payouts/AddRental");
    }

    [Fact]
    public void Choices_override_automatic()
    {
        Assert.Contains("business", Menu(Plain() with { PortfolioChoices = "Business=on" }).Select(s => s.Key));
        Assert.DoesNotContain("property", Menu(Plain() with { HasProperty = true, PortfolioChoices = "Property=off" }).Select(s => s.Key));
    }

    [Theory]
    [InlineData("Superfund", true)]
    [InlineData("Company", true)]
    [InlineData("Other", true)]
    [InlineData("Individual", false)]
    public void Entity_tax_pages_follow_the_entity_type(string entity, bool shown)
    {
        var smsf = Items(Menu(Plain(entity: entity))).Any(i => i.Page == "/TaxWork/Smsf");
        Assert.Equal(shown, smsf);
    }

    [Fact]
    public void Outside_Australia_the_australian_tax_pages_hide_but_country_reports_stay()
    {
        var items = Items(Menu(Plain(australia: false, entity: "Company") with { PortfolioChoices = "EntityTax=on" })).ToList();
        Assert.DoesNotContain(items, i => i.Page.StartsWith("/TaxWork/") || i.Page == "/Payouts/FundStatements");
        Assert.Contains(items, i => i.Page == "/SalesTax/Index");
        var tax = Menu(Plain(australia: false)).Single(s => s.Key == "tax");
        Assert.Contains(tax.Groups.SelectMany(g => g.Items), i => i.Page == "/Reports/CapitalGains");
    }

    [Fact]
    public void Practice_view_puts_clients_first()
    {
        var practice = Menu(Plain(portfolios: 3));
        Assert.Equal("clients", practice[0].Key);
        Assert.Equal("/Clients/Dashboard", new MenuContext(Plain(portfolios: 3), false, "GST").HomePage);
        // Clients aren't listed twice
        Assert.Single(Items(practice), i => i.Page == "/Clients/Index");

        var single = Menu(Plain(portfolios: 2));
        Assert.NotEqual("clients", single[0].Key);
        Assert.Contains(Items(single), i => i.Page == "/Clients/Index");
        Assert.Equal("clients", Menu(Plain() with { HasPracticeDetails = true })[0].Key);
        Assert.NotEqual("clients", Menu(Plain(portfolios: 9) with { LoginChoices = "Practice=off" })[0].Key);
    }

    [Fact]
    public void Admin_only_links_need_an_admin()
    {
        Assert.DoesNotContain(Items(Menu(Plain())), i => i.Page == "/Import/Prices");
        Assert.Contains(Items(Menu(Plain(), admin: true)), i => i.Page == "/Import/Prices");
    }

    [Fact]
    public void Hidden_areas_are_still_found_by_search()
    {
        var off = MainMenu.TurnedOff(new MenuContext(Plain(entity: "Company"), false, "GST"));
        Assert.Contains(off, x => x.Item.Page == "/Invoices/Index");
        Assert.Contains(off, x => x.Item.Page == "/Rentals/Index");
        Assert.DoesNotContain(off, x => x.Item.Page == "/Trades/Index");
        // Australian pages stay out for other countries, even in search
        var abroad = MainMenu.TurnedOff(new MenuContext(Plain(australia: false, entity: "Company"), false, "VAT"));
        Assert.DoesNotContain(abroad, x => x.Section.Key == "tax" && x.Item.Page.StartsWith("/TaxWork/"));
    }

    [Fact]
    public void Choices_read_write_and_merge()
    {
        var parsed = MenuAreas.Parse("Business=on; Property=off;Nonsense=on;Practice=maybe");
        Assert.Equal(MenuChoice.Show, parsed[MenuArea.Business]);
        Assert.Equal(MenuChoice.Hide, parsed[MenuArea.Property]);
        Assert.False(parsed.ContainsKey(MenuArea.Practice));
        Assert.Equal("Business=on;Property=off", MenuAreas.Format(parsed));
        Assert.Null(MenuAreas.Format(new Dictionary<MenuArea, MenuChoice> { [MenuArea.Business] = MenuChoice.Automatic }));
        Assert.Equal("Business=on;Practice=off",
            MenuAreas.Merge("Business=on;Property=off", new Dictionary<MenuArea, MenuChoice>
            {
                [MenuArea.Property] = MenuChoice.Automatic,
                [MenuArea.Practice] = MenuChoice.Hide,
            }));
    }

    [Fact]
    public void Before_the_migration_every_menu_shows()
    {
        var keys = Menu(MenuUsage.Everything(1, true)).Select(s => s.Key).ToList();
        Assert.Contains("business", keys);
        Assert.Contains("property", keys);
        Assert.Contains(Items(Menu(MenuUsage.Everything(1, true))), i => i.Page == "/TaxWork/Smsf");
    }
}
