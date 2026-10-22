using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Inventory;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.SalesTax;
using PortfolioManager.Web.Pages.Shared;

namespace PortfolioManager.Web.Services;

/// <summary>The accounts, tax codes, items and tracking options an invoice, bill, quote or purchase order's lines pick from.</summary>
public sealed record DocumentListSet(IReadOnlyList<LedgerAccount> Accounts, IReadOnlyList<TaxCode> TaxCodes, string TaxName,
    IReadOnlyList<InventoryItem> Items, TrackingSetup Tracking, bool Purchases)
{
    public IReadOnlySet<int> AccountIds => Accounts.Select(a => a.AccountId).ToHashSet();

    public DocumentLinesView View(IReadOnlyList<InvoiceLineInput> lines) =>
        new(lines, Accounts, Purchases ? "Account" : "Income account", TaxCodes, TaxName, Items, Tracking, Purchases);
}

public sealed class DocumentLists(ChartOfAccountsService chart, SalesTaxService salesTax, InventoryService inventory, TrackingService tracking)
{
    public static readonly DocumentListSet Empty = new([], [], "Tax", [], TrackingSetup.None, false);

    /// <summary>Sales lines go to income accounts; purchase lines to expense and asset accounts (stock, equipment, prepayments).</summary>
    public async Task<DocumentListSet> LoadAsync(int portfolioId, bool purchases)
    {
        var accounts = (await chart.GetAccountsAsync(portfolioId))
            .Where(a => !a.IsInactive && a.Postable != false && a.Number % 1000 != 0
                        && (purchases ? a.Type is "Expense" or "Asset" : a.Type == "Revenue"))
            .OrderBy(a => a.Type == "Expense" ? 0 : 1).ThenBy(a => a.Number)
            .ToList();
        IReadOnlyList<TaxCode> codes = [];
        var taxName = "Tax";
        if (await salesTax.GetSetupAsync(portfolioId) is { } setup)
        {
            taxName = setup.TaxName;
            codes = (await salesTax.GetActiveCodesAsync(portfolioId)).Where(c => c.IsSales != purchases).ToList();
        }
        IReadOnlyList<InventoryItem> items;
        try { items = await inventory.GetItemsAsync(portfolioId); }
        catch (Microsoft.Data.SqlClient.SqlException) { items = []; }
        return new DocumentListSet(accounts, codes, taxName, items, await tracking.GetAsync(portfolioId), purchases);
    }
}
