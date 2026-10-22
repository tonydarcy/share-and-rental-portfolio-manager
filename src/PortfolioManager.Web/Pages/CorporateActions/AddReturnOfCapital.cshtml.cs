using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.Validation;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.CorporateActions;

/// <summary>Record a return of capital: Preview shows each parcel's cost base reduction and any G1 gain, then the default handler saves it.</summary>
public class AddReturnOfCapitalModel(CurrentPortfolio current, CgtDiscountService cgt, CorporateActionsService actions) : PageModel
{
    [BindProperty]
    public CapitalReturnForm Input { get; set; } = new();

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<(string Exchange, string Code)> Holdings { get; private set; } = [];
    public NewCapitalReturn? Return { get; private set; }
    public CapitalReturnPreview? Preview { get; private set; }

    private IReadOnlyList<TradeLot> _trades = [];

    public async Task OnGetAsync(string? market, string? code)
    {
        if (!await LoadAsync())
            return;
        Input = new CapitalReturnForm { Market = market ?? "ASX", Code = code };
    }

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        if (await LoadAsync())
            await CheckAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
            return Page();
        if (!await CheckAsync() || Return is not { } r || Preview is not { } p)
            return Page();

        // Acts only on the selected portfolio
        var result = await actions.AddCapitalReturnAsync(Portfolio!.PortfolioId, r, p.Total);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }

        TempData["Message"] = $"Return of capital of {p.Total:C} on {r.Code} recorded. Cost base reduced by {p.Reduction:C}"
            + (p.Excess > 0 ? $"; {p.Excess:C} is a capital gain (G1)." : ".");
        return RedirectToPage("./Index");
    }

    private async Task<bool> CheckAsync()
    {
        var result = ReturnOfCapitalEntry.Validate(Input, FormRules.LatestToday(DateTime.UtcNow));
        foreach (var e in result.Errors)
        {
            var key = $"{nameof(Input)}.{e.Field}";
            if (ModelState[key] is not { Errors.Count: > 0 })
                ModelState.AddModelError(key, e.Message);
        }
        if (!ModelState.IsValid || result.Value is not { } r)
            return false;

        var id = Portfolio!.PortfolioId;
        Return = r;
        Preview = ReturnOfCapitalEntry.Preview(_trades, await actions.GetCapitalReturnLotsAsync(id), r,
            await actions.GetSplitFactorsAfterAsync(id, r.Market, r.Code, r.Date));

        if (Preview.Parcels.Count == 0)
        {
            ModelState.AddModelError(string.Empty, $"No {r.Code} units on {r.Market} were held in this portfolio at the end of {r.Date:d MMM yyyy}.");
            Preview = null;
            return false;
        }
        if (Preview.Total <= 0 || Preview.Total > FormRules.MaxMoney)
        {
            ModelState.AddModelError(string.Empty, "The total comes to less than a cent or is too large; check the amount.");
            Preview = null;
            return false;
        }
        return true;
    }

    private async Task<bool> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        _trades = await cgt.LoadTradesAsync(Portfolio.PortfolioId);
        Holdings = _trades
            .Select(t => (Exchange: t.ExchangeCode.Trim().ToUpperInvariant(), Code: t.StockCode.Trim().ToUpperInvariant()))
            .Distinct()
            .OrderBy(h => h.Code).ThenBy(h => h.Exchange)
            .ToList();
        return true;
    }
}
