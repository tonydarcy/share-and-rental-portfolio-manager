using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.Inventory;
using PortfolioManager.Core.SalesTax;

namespace PortfolioManager.Web.Pages.Shared;

/// <summary>
/// What the shared line editor (_DocumentLines) needs: the lines, the accounts and tax codes to pick from, and the
/// portfolio's items and tracking categories. Purchases picks each item's purchase price, account and tax code.
/// </summary>
public sealed record DocumentLinesView(
    IReadOnlyList<InvoiceLineInput> Lines,
    IReadOnlyList<LedgerAccount> Accounts,
    string AccountLabel,
    IReadOnlyList<TaxCode> TaxCodes,
    string TaxName,
    IReadOnlyList<InventoryItem> Items,
    TrackingSetup Tracking,
    bool Purchases,
    string Prefix = "Input.Lines");

/// <summary>What the shared currency fields (_CurrencyFields) need.</summary>
public sealed record CurrencyFieldsView(string? Currency, decimal? ExchangeRate, string HomeCurrency, string Prefix = "Input");
