using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Trades;

public class ParcelsModel(CurrentPortfolio current, ParcelChoiceService choices) : PageModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public RegionSettings Region { get; private set; } = RegionSettings.Australia;
    public SaleParcels? Sale { get; private set; }
    public CgtDiscountRate Rate { get; private set; } = CgtDiscountRate.None;

    [BindProperty]
    public ParcelMethod Method { get; set; } = ParcelMethod.Specific;

    /// <summary>Units picked from each parcel, by buy trade id.</summary>
    [BindProperty]
    public Dictionary<int, decimal> Units { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadAsync(id))
            return NotFound();
        if (Sale!.Choice is { } c)
        {
            Method = c.Method;
            Units = c.Picks.ToDictionary(p => p.BuyTradeId, p => p.Units);
        }
        else
            Units = Sale.Events.Where(e => e.BuyTradeId > 0).GroupBy(e => e.BuyTradeId).ToDictionary(g => g.Key, g => g.Sum(e => e.Units));
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        if (!await LoadAsync(id))
            return NotFound();

        var picks = Method switch
        {
            ParcelMethod.MinGain or ParcelMethod.MaxGain =>
                ParcelChoices.Plan(Method, Sale!.Held, Sale.Units, DateOnly.FromDateTime(Sale.Sale.Date), Sale.ProceedsPerUnit, Rate.Rate),
            ParcelMethod.Fifo => null,
            _ => Units.Select(u => new ParcelPick(u.Key, u.Value)).ToList(),
        };
        var result = picks is null
            ? await choices.ResetAsync(Portfolio!.PortfolioId, id)
            : await choices.SaveAsync(Portfolio!.PortfolioId, Sale!, Method, picks);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error ?? "Not saved.");
            return Page();
        }
        TempData["Message"] = picks is null
            ? "This sale is back to first in, first out."
            : $"Saved: this sale now takes the parcels shown ({ParcelChoices.Label(Method).ToLowerInvariant()}). The capital gains and the sale's journal follow.";
        return RedirectToPage(new { id });
    }

    private async Task<bool> LoadAsync(int id)
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        Region = await current.GetRegionAsync();
        Rate = Region.IsAustralia ? CgtDiscount.RateFor(Portfolio.EntityType) : CgtDiscountRate.None;
        // Only a sale in the selected portfolio
        Sale = await choices.GetSaleAsync(Portfolio.PortfolioId, id);
        return Sale is not null;
    }
}
