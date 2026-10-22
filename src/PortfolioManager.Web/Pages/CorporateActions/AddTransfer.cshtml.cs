using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.Validation;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.CorporateActions;

/// <summary>Record a demerger, takeover, code change or worthless shares: Preview shows each parcel's part, then the default handler saves it.</summary>
public class AddTransferModel(CurrentPortfolio current, CgtDiscountService cgt, ParcelTransferService transfers) : PageModel
{
    [BindProperty]
    public TransferForm Input { get; set; } = new();

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<(string Exchange, string Code)> Holdings { get; private set; } = [];
    public NewParcelTransfer? Transfer { get; private set; }
    public TransferPreview? Preview { get; private set; }

    public async Task OnGetAsync(string? kind, string? market, string? code)
    {
        if (!await LoadAsync())
            return;
        Input = new TransferForm
        {
            Kind = TransferKinds.All.FirstOrDefault(k => k.Equals(kind, StringComparison.OrdinalIgnoreCase)) ?? ParcelTransfer.Demerger,
            Market = market ?? "ASX",
            Code = code,
            NewMarket = market ?? "ASX",
            Rollover = true,
        };
    }

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        if (await LoadAsync())
            await CheckAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync() || !await CheckAsync() || Transfer is not { } t || Preview is not { } p)
            return Page();

        // Acts only on the selected portfolio
        var result = await transfers.ApplyAsync(Portfolio!.PortfolioId, t, p);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }
        TempData["Message"] = t switch
        {
            { IsDemerger: true } => $"Demerger recorded: {t.NewUnits:N0} {t.NewCode} shares with {p.CostMoved:C} of the {t.Code} cost base.",
            { IsTakeover: true } => $"Takeover of {t.Code} recorded" + (t.NewUnits > 0 ? $": {t.NewUnits:N0} {t.NewCode} shares" : "") + (p.Cash > 0 ? $" and {p.Cash:C} cash." : "."),
            { IsExercise: true } => $"{p.UnitsHeld:#,##0} {t.Code} exercised for {t.NewUnits:N0} {t.NewCode} shares, cost base {p.CostMoved + t.ExercisePrice * t.NewUnits:C}.",
            { IsCodeChange: true } => $"{t.Code} is now {t.NewCode} on {t.NewMarket}.",
            _ => $"{p.UnitsHeld:#,##0} {t.Code} shares recorded as sold for nil, a capital loss of {p.CostBase:C}.",
        };
        return RedirectToPage("./Index");
    }

    private async Task<bool> CheckAsync()
    {
        var result = ParcelTransferEntry.Validate(Input, FormRules.LatestToday(DateTime.UtcNow));
        foreach (var e in result.Errors)
        {
            var key = $"{nameof(Input)}.{e.Field}";
            if (ModelState[key] is not { Errors.Count: > 0 })
                ModelState.AddModelError(key, e.Message);
        }
        if (!ModelState.IsValid || result.Value is not { } t)
            return false;

        Transfer = t;
        Preview = await transfers.PreviewAsync(Portfolio!.PortfolioId, t);
        if (!t.IsCodeChange && Preview.Parcels.Count == 0)
        {
            ModelState.AddModelError(string.Empty, $"No {t.Code} units on {t.Market} were held in this portfolio at the end of {t.Date:d MMM yyyy}.");
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
        Holdings = (await cgt.LoadTradesAsync(Portfolio.PortfolioId))
            .Select(t => (Exchange: t.ExchangeCode.Trim().ToUpperInvariant(), Code: t.StockCode.Trim().ToUpperInvariant()))
            .Distinct()
            .OrderBy(h => h.Code).ThenBy(h => h.Exchange)
            .ToList();
        return true;
    }
}
