namespace PortfolioManager.Core.Portfolios;

/// <summary>Parts of the top menu that only show when a portfolio (or login) uses them.</summary>
public enum MenuArea
{
    /// <summary>Invoices, bills, orders, stock, fixed assets and expense claims.</summary>
    Business,
    /// <summary>Rental properties.</summary>
    Property,
    /// <summary>Company, trust and SMSF tax pages.</summary>
    EntityTax,
    /// <summary>The practice view: a Clients menu first, home is the practice dashboard. Kept per login.</summary>
    Practice,
}

/// <summary>What a portfolio's menu choice for an area is.</summary>
public enum MenuChoice { Automatic, Show, Hide }

/// <summary>What the menu needs to decide what to show (uspGetMenuUsage), plus facts the site already knows.</summary>
public sealed record MenuUsage(
    bool HasBusiness,
    bool HasProperty,
    bool HasPracticeDetails,
    string EntityType,
    string? PortfolioChoices,
    string? LoginChoices,
    int PortfolioCount = 1,
    bool IsAustralia = true)
{
    /// <summary>Used before the menu migration has run: every menu shows, as it did before.</summary>
    public static MenuUsage Everything(int portfolioCount, bool isAustralia) =>
        new(true, true, false, "", "Business=on;Property=on;EntityTax=on", null, portfolioCount, isAustralia);
}

/// <summary>Reads and writes the menu choices and decides which areas show.</summary>
public static class MenuAreas
{
    /// <summary>Portfolios a login can open before it is treated as an accounting practice.</summary>
    public const int PracticePortfolioCount = 3;

    public static string Label(MenuArea area) => area switch
    {
        MenuArea.Business => "Business (invoices, bills, stock, fixed assets)",
        MenuArea.Property => "Property (rental properties)",
        MenuArea.EntityTax => "Company, trust and SMSF tax pages",
        MenuArea.Practice => "Practice view (Clients menu first, for accountants)",
        _ => area.ToString(),
    };

    /// <summary>The areas chosen on a portfolio's settings (Practice is chosen per login).</summary>
    public static readonly IReadOnlyList<MenuArea> PortfolioAreas = [MenuArea.Business, MenuArea.Property, MenuArea.EntityTax];

    /// <summary>Reads text such as 'Business=on;Property=off'. Unknown or missing parts mean automatic.</summary>
    public static Dictionary<MenuArea, MenuChoice> Parse(string? text)
    {
        var result = new Dictionary<MenuArea, MenuChoice>();
        foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !Enum.TryParse<MenuArea>(pair[0], true, out var area))
                continue;
            if (pair[1].Equals("on", StringComparison.OrdinalIgnoreCase))
                result[area] = MenuChoice.Show;
            else if (pair[1].Equals("off", StringComparison.OrdinalIgnoreCase))
                result[area] = MenuChoice.Hide;
        }
        return result;
    }

    /// <summary>Writes choices back as text, leaving automatic ones out. Null when everything is automatic.</summary>
    public static string? Format(IReadOnlyDictionary<MenuArea, MenuChoice> choices)
    {
        var text = string.Join(';', choices
            .Where(c => c.Value != MenuChoice.Automatic)
            .OrderBy(c => c.Key)
            .Select(c => $"{c.Key}={(c.Value == MenuChoice.Show ? "on" : "off")}"));
        return text.Length == 0 ? null : text;
    }

    /// <summary>Changes some choices in saved text and keeps the rest (such as a login's Practice choice).</summary>
    public static string? Merge(string? saved, IReadOnlyDictionary<MenuArea, MenuChoice> changes)
    {
        var choices = Parse(saved);
        foreach (var (area, choice) in changes)
            choices[area] = choice;
        return Format(choices);
    }

    public static MenuChoice ChoiceFor(MenuArea area, MenuUsage usage) =>
        Parse(area == MenuArea.Practice ? usage.LoginChoices : usage.PortfolioChoices).GetValueOrDefault(area);

    /// <summary>Whether the area shows automatically, before any choice.</summary>
    public static bool ShowsAutomatically(MenuArea area, MenuUsage usage) => area switch
    {
        MenuArea.Business => usage.HasBusiness,
        MenuArea.Property => usage.HasProperty,
        // Company, trust (Other) and SMSF pages for those entity types; they follow Australian rules
        MenuArea.EntityTax => usage.IsAustralia && !string.IsNullOrWhiteSpace(usage.EntityType)
                              && !usage.EntityType.Trim().Equals("Individual", StringComparison.OrdinalIgnoreCase),
        MenuArea.Practice => usage.HasPracticeDetails || usage.PortfolioCount >= PracticePortfolioCount,
        _ => true,
    };

    public static bool Shows(MenuArea area, MenuUsage usage) => ChoiceFor(area, usage) switch
    {
        MenuChoice.Show => true,
        MenuChoice.Hide => false,
        _ => ShowsAutomatically(area, usage),
    };
}
