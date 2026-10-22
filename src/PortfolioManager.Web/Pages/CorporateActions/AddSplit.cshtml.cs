using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.Validation;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.CorporateActions;

/// <summary>Record a split or consolidation: Preview shows the holding before and after, then the default handler saves it.</summary>
public class AddSplitModel(CurrentPortfolio current, CgtDiscountService cgt, CorporateActionsService actions) : PageModel
{
    [BindProperty]
    public SplitForm Input { get; set; } = new();

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<(string Exchange, string Code)> Holdings { get; private set; } = [];
    public NewSplit? Split { get; private set; }
    public SplitPreview? Preview { get; private set; }

    private IReadOnlyList<TradeLot> _trades = [];

    public async Task OnGetAsync(string? market, string? code)
    {
        if (!await LoadAsync())
            return;
        Input = new SplitForm { Market = market ?? "ASX", Code = code, NewUnits = 2, OldUnits = 1 };
    }

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        if (await LoadAsync())
            Check();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
            return Page();
        if (!Check() || Split is not { } split)
            return Page();

        // Acts only on the selected portfolio's trades
        var result = await actions.ApplySplitAsync(Portfolio!.PortfolioId, split);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }

        TempData["Message"] = $"{split.Ratio.Kind} of {split.Code} ({split.Ratio}) from {split.Date:d MMM yyyy} recorded: "
            + $"{Preview!.TradesAdjusted:N0} earlier trade(s) changed to the new units.";
        return RedirectToPage("./Index");
    }

    /// <summary>Validates the form and works out the preview. False (with errors on the page) when it can't be saved.</summary>
    private bool Check()
    {
        var result = ShareSplit.Validate(Input, FormRules.LatestToday(DateTime.UtcNow));
        foreach (var e in result.Errors)
        {
            var key = $"{nameof(Input)}.{e.Field}";
            if (ModelState[key] is not { Errors.Count: > 0 })
                ModelState.AddModelError(key, e.Message);
        }
        if (!ModelState.IsValid || result.Value is not { } split)
            return false;

        Split = split;
        Preview = ShareSplit.Preview(_trades, split.Market, split.Code, split.Date, split.Ratio);
        if (Preview.TradesAdjusted == 0)
        {
            ModelState.AddModelError(string.Empty, $"There are no {split.Code} trades on {split.Market} before {split.Date:d MMM yyyy} in this portfolio, so there's nothing to change.");
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
