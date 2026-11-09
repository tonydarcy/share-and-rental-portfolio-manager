using PortfolioManager.Core.Portfolios;

namespace PortfolioManager.Web.Services;

/// <summary>One link in the top menu. <see cref="Kind"/> is the only route value any menu link needs (Orders).</summary>
public sealed record MenuItem(string Label, string Page, string? Kind = null)
{
    /// <summary>Shows only when this area is in use (see <see cref="MenuAreas"/>).</summary>
    public MenuArea? Area { get; init; }
    /// <summary>Follows Australian rules only, so it hides for portfolios in other countries.</summary>
    public bool AustraliaOnly { get; init; }
    public bool AdminOnly { get; init; }
    /// <summary>Other words people might type in Go to, such as "GST" for the sales tax report.</summary>
    public string Keywords { get; init; } = "";
}

public sealed record MenuGroup(string Heading, IReadOnlyList<MenuItem> Items)
{
    public MenuArea? Area { get; init; }
    public bool AustraliaOnly { get; init; }
}

public sealed record MenuSection(string Key, string Label, string Icon, IReadOnlyList<MenuGroup> Groups)
{
    public MenuArea? Area { get; init; }
}

/// <summary>Who is looking, and at what, for deciding which menu links show.</summary>
public sealed record MenuContext(MenuUsage Usage, bool IsAdmin, string SalesTaxName)
{
    public bool Shows(MenuArea area) => MenuAreas.Shows(area, Usage);
    public bool PracticeView => Shows(MenuArea.Practice);
    public string HomePage => PracticeView ? "/Clients/Dashboard" : "/Index";
}

/// <summary>
/// The site's top menu, in one place. The layout draws it three ways from this list: wide grouped menus on a
/// computer, a side panel on a phone, and the Go to search.
/// </summary>
public static class MainMenu
{
    public static IReadOnlyList<MenuSection> For(MenuContext c)
    {
        var all = All(c);
        var result = new List<MenuSection>();
        foreach (var section in all)
        {
            if (section.Area is { } sa && !c.Shows(sa))
                continue;
            var groups = section.Groups
                .Where(g => (g.Area is not { } ga || c.Shows(ga)) && (!g.AustraliaOnly || c.Usage.IsAustralia))
                .Select(g => g with
                {
                    Items = g.Items.Where(i => (i.Area is not { } ia || c.Shows(ia))
                                               && (!i.AustraliaOnly || c.Usage.IsAustralia)
                                               && (!i.AdminOnly || c.IsAdmin)).ToList(),
                })
                .Where(g => g.Items.Count > 0)
                .ToList();
            if (groups.Count > 0)
                result.Add(section with { Groups = groups });
        }
        return result;
    }

    /// <summary>
    /// Links hidden only because the portfolio doesn't use that part yet (Business, Property, company/trust/SMSF tax).
    /// Go to still finds them, so the first invoice or property can be added. Australian-only pages are not included.
    /// </summary>
    public static IReadOnlyList<(MenuSection Section, MenuGroup Group, MenuItem Item)> TurnedOff(MenuContext c)
    {
        var everything = c with { Usage = c.Usage with { PortfolioChoices = MenuAreas.Merge(c.Usage.PortfolioChoices,
            MenuAreas.PortfolioAreas.ToDictionary(a => a, _ => MenuChoice.Show)) } };
        var shown = For(c).SelectMany(s => s.Groups).SelectMany(g => g.Items).Select(i => (i.Page, i.Kind)).ToHashSet();
        return [.. For(everything)
            .SelectMany(s => s.Groups.SelectMany(g => g.Items.Select(i => (Section: s, Group: g, Item: i))))
            .Where(x => !shown.Contains((x.Item.Page, x.Item.Kind)))];
    }

    /// <summary>Every link, before anything is hidden. Tests check each page exists.</summary>
    public static IReadOnlyList<MenuSection> All(MenuContext c)
    {
        var practice = c.PracticeView;
        List<MenuItem> clientItems =
        [
            new("Practice dashboard", "/Clients/Dashboard"),
            new("Client list", "/Clients/Index"),
            new("Client queries", "/Clients/Queries"),
            new("Onboarding new clients", "/Clients/Onboarding"),
        ];
        var sections = new List<MenuSection>();
        if (practice)
        {
            sections.Add(new("clients", "Clients", "bi-people",
            [
                new("Practice", [.. clientItems,
                    new("Consolidated (family group)", "/Reports/Consolidated"),
                    new("Practice details", "/Clients/Practice")]),
            ]));
        }
        sections.AddRange(
        [
            new("investments", "Investments", "bi-graph-up",
            [
                new("Holdings", [
                    new("Holdings", "/Holdings"),
                    new("Groups", "/Groups/Index"),
                    new("Term deposits and bonds", "/Debt/FixedIncome"),
                    new("Margin and investment loans", "/Debt/Loans"),
                ]),
                new("Transactions", [
                    new("Share trades", "/Trades/Index"),
                    new("Cash transactions", "/Cash/Index"),
                    new("Payouts", "/Payouts/Index") { Keywords = "dividends distributions" },
                    new("Foreign currency trades", "/Trades/FxCheck"),
                ]),
                new("Corporate actions", [
                    new("Corporate actions", "/CorporateActions/Index"),
                    new("Announced dividends and splits", "/CorporateActions/Announced"),
                ]),
                new("Statements", [
                    new("Fund tax statements (AMMA)", "/Payouts/FundStatements") { AustraliaOnly = true, Keywords = "AMIT" },
                    new("Employee share schemes", "/Payouts/EmployeeShares") { AustraliaOnly = true, Keywords = "ESS" },
                ]),
            ]),
            new("add", "Add", "bi-plus-circle",
            [
                new("Record", [
                    new("Trade", "/Trades/Add") { Keywords = "buy sell" },
                    new("Payout", "/Payouts/Add") { Keywords = "dividend distribution" },
                    new("DRP", "/Payouts/AddDrp") { Keywords = "dividend reinvestment" },
                    new("Cash transaction", "/Cash/Add"),
                    new("Rental payout", "/Payouts/AddRental") { Area = MenuArea.Property },
                ]),
                new("Crypto", [
                    new("Crypto swap", "/Trades/CryptoSwap"),
                    new("Crypto reward", "/Payouts/AddCryptoReward") { Keywords = "staking airdrop" },
                ]),
                new("Corporate actions", [
                    new("Split or consolidation", "/CorporateActions/AddSplit"),
                    new("Return of capital", "/CorporateActions/AddReturnOfCapital"),
                    new("Demerger, takeover or option exercise", "/CorporateActions/AddTransfer"),
                ]),
                new("Import", [
                    new("Share trades (CSV)", "/Import/Trades"),
                    new("Crypto exchange files (CSV)", "/Import/Crypto"),
                    new("Navexa sync (API)", "/Import/NavexaSync"),
                    new("Navexa files (CSV)", "/Import/Navexa"),
                    new("Bank statement", "/Accounting/BankImport"),
                    new("Share prices (CSV)", "/Import/Prices") { AdminOnly = true },
                    new("Dividend details (CSV)", "/Import/DividendDetails") { AdminOnly = true },
                ]),
            ]),
            new("business", "Business", "bi-briefcase",
            [
                new("Sales", [
                    new("Invoices", "/Invoices/Index"),
                    new("New invoice", "/Invoices/Edit"),
                    new("Customers", "/Invoices/Customers"),
                    new("Repeating invoices", "/Invoices/Repeating"),
                    new("Quotes", "/Orders/Index", "Quote"),
                    new("Aged receivables", "/Invoices/Aged") { Keywords = "debtors" },
                    new("Customer statement", "/Invoices/Statement"),
                    new("Invoice settings", "/Invoices/Settings"),
                ]),
                new("Purchases", [
                    new("Bills", "/Bills/Index"),
                    new("New bill", "/Bills/Edit"),
                    new("Suppliers", "/Bills/Suppliers"),
                    new("Repeating bills", "/Bills/Repeating"),
                    new("Aged payables", "/Bills/Aged") { Keywords = "creditors" },
                    new("Purchase orders", "/Orders/Index", "PurchaseOrder"),
                    new("Read a receipt or bill", "/Reading/Index") { Keywords = "scan photo" },
                    new("Expense claims", "/Claims/Index"),
                ]),
                new("Stock and assets", [
                    new("Inventory", "/Inventory/Index") { Keywords = "stock" },
                    new("Stocktake", "/Inventory/Stocktake"),
                    new("Fixed assets", "/Assets/Index"),
                    new("Post depreciation", "/Assets/Depreciate"),
                ]),
            ]) { Area = MenuArea.Business },
            new("property", "Property", "bi-house",
            [
                new("Properties", [
                    new("Properties", "/Rentals/Index") { Keywords = "rental" },
                    new("Yearly figures and assets", "/TaxWork/Rental"),
                ]),
                new("Reports", [
                    new("Rental schedule", "/Rentals/Schedule"),
                    new("Income and expenses", "/Rentals/Statement"),
                    new("Year on year", "/Rentals/Compare"),
                    new("Depreciation schedule", "/Rentals/Depreciation"),
                    new("Review checklist", "/Rentals/Review"),
                ]),
            ]) { Area = MenuArea.Property },
            new("reports", "Reports", "bi-bar-chart",
            [
                new("Performance", [
                    new("Performance", "/Reports/Performance") { Keywords = "returns" },
                    new("Contribution", "/Reports/Contribution"),
                    new("Consolidated (family group)", "/Reports/Consolidated"),
                    new("Diversity", "/Reports/Diversity") { Keywords = "allocation" },
                    new("Income calendar", "/Reports/IncomeCalendar"),
                ]),
                new("Gains and income", [
                    new("Realised capital gains", "/Reports/CapitalGains") { Keywords = "CGT" },
                    new("Unrealised capital gains", "/Reports/UnrealisedGains") { Keywords = "CGT" },
                    new("What-if sale", "/Reports/WhatIf"),
                    new("Tax-loss selling", "/Reports/TaxLoss"),
                    new("Taxable income", "/Reports/TaxableIncome"),
                ]),
                new("Prices", [
                    new("Share quote", "/Prices/Quote"),
                    new("Share trends", "/Prices/Trends"),
                    new("Manual prices (funds, unlisted, property)", "/Prices/Manual"),
                    new("Fund look-through", "/Prices/LookThrough"),
                ]),
            ]),
            new("accounting", "Accounting", "bi-journal-text",
            [
                new("Ledger", [
                    new("Chart of accounts", "/Accounting/Accounts"),
                    new("Journals", "/Accounting/Journals"),
                    new("New journal entry", "/Accounting/JournalEntry"),
                    new("Repeating journals", "/Accounting/Repeating"),
                    new("Attachments", "/Attachments/Index"),
                ]),
                new("Bank", [
                    new("Import bank statement", "/Accounting/BankImport"),
                    new("Import history", "/Accounting/ImportHistory"),
                    new("Unallocated transactions", "/Accounting/Unallocated") { Keywords = "code coding" },
                    new("Allocation rules", "/Accounting/Rules"),
                    new("Bank reconciliation", "/Accounting/Reconcile"),
                ]),
                new("Reports", [
                    new("Trial balance", "/Accounting/TrialBalance"),
                    new("Profit & loss", "/Accounting/ProfitLoss") { Keywords = "P&L income statement" },
                    new("Balance sheet", "/Accounting/BalanceSheet"),
                    new("Cash movement", "/Accounting/CashMovement"),
                    new("Financial statements", "/Accounting/Statements"),
                    new("Report pack (print)", "/Accounting/ReportPack"),
                ]),
                new("Tracking and budgets", [
                    new("Tracking categories", "/Accounting/Tracking"),
                    new("Profit & loss by tracking", "/Accounting/TrackingReport"),
                    new("Budgets", "/Accounting/Budgets"),
                ]),
                new("Year end", [
                    new("Foreign currency accounts", "/Accounting/Currency"),
                    new("Foreign exchange revaluation", "/Accounting/FxRevaluation"),
                    new("Market value revaluation", "/Accounting/Revaluation"),
                    new("Lock periods", "/Accounting/Lock"),
                    new("Audit trail", "/Accounting/AuditTrail"),
                    new("Export to Xero or MYOB", "/Accounting/Export"),
                ]),
            ]),
            new("tax", "Tax", "bi-percent",
            [
                new("Tax return", [
                    new("Income tax reconciliation", "/TaxWork/Reconciliation"),
                    new("Tax return labels", "/TaxWork/ReturnLabels"),
                    new("Paper tax return (print)", "/TaxWork/PaperReturn"),
                    new("ATO prefill", "/TaxWork/Prefill"),
                    new("Tax planning", "/TaxWork/Planning"),
                    new("Workpapers and year-end checklist", "/TaxWork/Workpapers"),
                    new("Shares as trading stock", "/TaxWork/TradingStock"),
                    new("Rental properties", "/TaxWork/Rental") { Area = MenuArea.Property },
                    new("ESS annual report (employers)", "/TaxWork/EssReport"),
                ]) { AustraliaOnly = true },
                new("Companies, trusts and SMSFs", [
                    new("Trust distribution", "/TaxWork/Distribution"),
                    new("SMSF members", "/TaxWork/Smsf") { Keywords = "super" },
                    new("SMSF TBAR, ECPI and strategy", "/TaxWork/SmsfExtras"),
                    new("SMSF audit pack", "/TaxWork/SmsfAudit"),
                    new("Company tax and franking", "/TaxWork/CompanyTax"),
                    new("Division 7A calculator", "/TaxWork/Div7a"),
                ]) { Area = MenuArea.EntityTax, AustraliaOnly = true },
                new(c.SalesTaxName, [
                    new($"{c.SalesTaxName} report", "/SalesTax/Index") { Keywords = "BAS VAT GST sales tax" },
                    new($"{c.SalesTaxName} codes", "/SalesTax/Codes"),
                ]),
            ]),
        ]);
        if (!practice)
        {
            // Without the practice view the client pages sit at the end of Tax, as before
            var tax = sections[^1];
            sections[^1] = tax with { Groups = [.. tax.Groups, new MenuGroup("Clients", clientItems)] };
        }
        if (!c.Usage.IsAustralia)
        {
            // Outside Australia the tax return pages hide, so Tax also lists the reports that follow the country
            var tax = sections[^1];
            sections[^1] = tax with
            {
                Groups = [new MenuGroup("Capital gains and income", [
                    new("Realised capital gains", "/Reports/CapitalGains"),
                    new("Unrealised capital gains", "/Reports/UnrealisedGains"),
                    new("Taxable income", "/Reports/TaxableIncome"),
                ]), .. tax.Groups],
            };
        }
        return sections;
    }
}
