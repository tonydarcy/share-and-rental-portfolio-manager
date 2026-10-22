using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Orders;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Orders;

/// <summary>Quotes to customers, or purchase orders to suppliers.</summary>
public class IndexModel(CurrentPortfolio current, QuoteOrderService orders, ILogger<IndexModel> logger) : BooksPageModel(current, logger)
{
    [BindProperty(SupportsGet = true)]
    public string Kind { get; set; } = QuoteOrderKinds.Quote;

    public IReadOnlyList<QuoteOrder> Documents { get; private set; } = [];
    public bool IsQuote => Kind == QuoteOrderKinds.Quote;
    public string Plural => IsQuote ? "Quotes" : "Purchase orders";

    public async Task OnGetAsync()
    {
        if (Kind != QuoteOrderKinds.PurchaseOrder)
            Kind = QuoteOrderKinds.Quote;
        if (await LoadPortfolioAsync())
            Documents = await orders.GetAsync(PortfolioId, Kind);
    }

    public static string Badge(string status) => status switch
    {
        QuoteOrderStatus.Accepted or QuoteOrderStatus.Invoiced or QuoteOrderStatus.Billed => "text-bg-success",
        QuoteOrderStatus.Sent => "text-bg-primary",
        QuoteOrderStatus.Declined or QuoteOrderStatus.Cancelled => "text-bg-secondary",
        _ => "text-bg-light border",
    };
}
