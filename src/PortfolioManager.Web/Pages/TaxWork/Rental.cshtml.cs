using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Statements;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>The rental property schedule: each property's income and expenses for the year, its depreciating assets and capital works, and item 21.</summary>
public class RentalModel(CurrentPortfolio current, RentalService rentals, LockedPeriodService locks) : PageModel
{
    public sealed class PropertyInput
    {
        public int Id { get; set; }
        public string? Address { get; set; }
        public DateOnly? FirstEarned { get; set; }
        public decimal OwnershipPct { get; set; } = 100;
        public bool Active { get; set; } = true;
        public string? Note { get; set; }
    }

    public sealed class AssetInput
    {
        public int Id { get; set; }
        public int PropertyId { get; set; }
        public string Kind { get; set; } = RentalAssetKinds.Plant;
        public string? Description { get; set; }
        public DateOnly? Start { get; set; }
        public decimal Cost { get; set; }
        public decimal? LifeYears { get; set; }
        public string Method { get; set; } = RentalAssetKinds.DiminishingValue;
        public decimal? Rate { get; set; } = 2.5m;
        public DateOnly? Disposed { get; set; }
    }

    [BindProperty(SupportsGet = true)] public int? Fy { get; set; }
    [BindProperty] public PropertyInput Property { get; set; } = new();
    [BindProperty] public AssetInput Asset { get; set; } = new();
    [BindProperty] public Dictionary<string, decimal?> Amounts { get; set; } = [];

    public int Year { get; private set; }
    public PortfolioSummary? Portfolio { get; private set; }
    public RentalSchedule Schedule { get; private set; } = RentalSchedule.None;
    public bool Locked { get; private set; }
    public IReadOnlyList<int> Years => FinancialYear.Recent(DateOnly.FromDateTime(DateTime.Today));

    public async Task OnGetAsync()
    {
        if (await LoadAsync() is { } p)
            Schedule = await rentals.GetYearAsync(p.PortfolioId, Year);
    }

    public async Task<IActionResult> OnPostPropertyAsync()
    {
        if (await LoadAsync() is not { } p)
            return Forbid();
        var r = await rentals.SavePropertyAsync(p.PortfolioId, Property.Id, Property.Address, Property.FirstEarned, Property.OwnershipPct, Property.Active, Property.Note);
        return Done(r, Property.Id == 0 ? "Property added." : "Property saved.");
    }

    public async Task<IActionResult> OnPostDeletePropertyAsync(int id)
    {
        if (await LoadAsync() is not { } p)
            return Forbid();
        return Done(await rentals.DeletePropertyAsync(p.PortfolioId, id), "Property deleted, with its figures and assets for every year.");
    }

    public async Task<IActionResult> OnPostAmountsAsync(int id)
    {
        if (await LoadAsync() is not { } p)
            return Forbid();
        if (Locked)
            return Done(ProcResult.Failed($"{FinancialYear.Label(Year)} is locked."), "");
        var known = RentalCategories.All.Select(c => c.Key).Append(RentalCategories.Weeks).ToHashSet();
        var amounts = Amounts.Where(a => known.Contains(a.Key) && a.Value is { } v && v != 0).ToDictionary(a => a.Key, a => a.Value!.Value);
        if (amounts.GetValueOrDefault(RentalCategories.Weeks) is < 0 or > 53)
            return Done(ProcResult.Failed("Weeks rented must be between 0 and 53."), "");
        return Done(await rentals.SaveAmountsAsync(p.PortfolioId, id, Year, amounts), $"Saved the {FinancialYear.Label(Year)} figures.");
    }

    public async Task<IActionResult> OnPostAssetAsync()
    {
        if (await LoadAsync() is not { } p)
            return Forbid();
        if (Asset.Start is null || string.IsNullOrWhiteSpace(Asset.Description))
            return Done(ProcResult.Failed("Enter what the asset is and the date it was first used or held for renting."), "");
        var plant = Asset.Kind == RentalAssetKinds.Plant;
        var a = new RentalAsset(Asset.Id, Asset.PropertyId, plant ? RentalAssetKinds.Plant : RentalAssetKinds.Building, Asset.Description.Trim(), Asset.Start.Value,
            Asset.Cost, plant ? Asset.LifeYears : null, plant ? Asset.Method : null, plant ? null : Asset.Rate, Asset.Disposed);
        return Done(await rentals.SaveAssetAsync(p.PortfolioId, a), plant ? "Depreciating asset saved." : "Capital works saved.");
    }

    public async Task<IActionResult> OnPostDeleteAssetAsync(int id)
    {
        if (await LoadAsync() is not { } p)
            return Forbid();
        return Done(await rentals.DeleteAssetAsync(p.PortfolioId, id), "Asset deleted.");
    }

    private IActionResult Done(ProcResult r, string message)
    {
        TempData[r.Ok ? "Message" : "Error"] = r.Ok ? message : r.Error;
        return RedirectToPage(new { fy = Year });
    }

    private async Task<PortfolioSummary?> LoadAsync()
    {
        Year = StatementFormat.ClampYear(Fy, FinancialYear.LastCompleted(DateOnly.FromDateTime(DateTime.Today)));
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is not null)
            Locked = (await locks.GetAsync(Portfolio.PortfolioId)).IsYearLocked(Year);
        return Portfolio;
    }
}
