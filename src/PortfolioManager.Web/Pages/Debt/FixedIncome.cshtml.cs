using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Debt;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Debt;

/// <summary>Term deposits, bonds and hybrids: interest earned in a financial year and accrued at its end.</summary>
public class FixedIncomeModel(CurrentPortfolio current, FixedIncomeService service, InterestAccrualService accruals) : PageModel
{
    public sealed class HoldingForm
    {
        public int Id { get; set; }
        public string Kind { get; set; } = FixedIncomeKinds.TermDeposit;
        public string? Name { get; set; }
        public string? ExchangeCode { get; set; }
        public string? StockCode { get; set; }
        public decimal? FaceValue { get; set; }
        public decimal? RatePercent { get; set; }
        public DateOnly? Start { get; set; }
        public DateOnly? Maturity { get; set; }
        public string Frequency { get; set; } = InterestFrequency.AtMaturity;
        public string? Comment { get; set; }
    }

    [BindProperty] public HoldingForm Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public int? Fy { get; set; }
    [BindProperty(SupportsGet = true)] public int? Edit { get; set; }
    [TempData] public string? Message { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public int Year { get; private set; }
    public IReadOnlyList<int> Years { get; private set; } = [];
    public IReadOnlyList<FixedIncomeYear> Rows { get; private set; } = [];
    public InterestAccruals? Accruals { get; private set; }

    public async Task OnGetAsync()
    {
        if (!await LoadAsync())
            return;
        if (Edit is { } id && Rows.FirstOrDefault(r => r.Holding.Id == id)?.Holding is { } h)
            Input = new HoldingForm
            {
                Id = h.Id, Kind = h.Kind, Name = h.Name, ExchangeCode = h.ExchangeCode, StockCode = h.StockCode, FaceValue = h.FaceValue,
                RatePercent = h.RatePercent, Start = h.Start, Maturity = h.Maturity, Frequency = h.Frequency, Comment = h.Comment,
            };
    }

    /// <summary>Posts the year-end interest accruals (and their reversal the next day) to the ledger.</summary>
    public async Task<IActionResult> OnPostAccrueAsync()
    {
        if (!await LoadAsync())
            return Page();
        var result = await accruals.PostAsync(Portfolio!.PortfolioId, Year);
        Message = result.Ok ? $"The interest accruals at the end of {FinancialYear.Label(Year)} are posted, and reversed the next day." : result.Error;
        return RedirectToPage(new { fy = Year });
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!await LoadAsync())
            return Page();
        var i = Input;
        var h = new FixedIncomeHolding(i.Id, i.Kind, i.Name?.Trim() ?? "", i.ExchangeCode, i.StockCode, Math.Round(i.FaceValue ?? 0, 2), i.RatePercent ?? 0,
            i.Start ?? DateOnly.FromDateTime(DateTime.Today), i.Maturity, i.Frequency, i.Comment);
        foreach (var e in FixedIncomeCalc.Validate(h))
            ModelState.AddModelError(string.Empty, e);
        if (!ModelState.IsValid)
            return Page();
        // The proc checks an edited holding belongs to the selected portfolio
        var r = await service.SaveAsync(Portfolio!.PortfolioId, h);
        Message = r.Ok ? $"{h.Name} is saved." : r.Error;
        return RedirectToPage(new { fy = Fy });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (await current.GetSelectedAsync() is not { } p)
            return RedirectToPage();
        var r = await service.DeleteAsync(p.PortfolioId, id);
        Message = r.Ok ? "Deleted." : r.Error;
        return RedirectToPage(new { fy = Fy });
    }

    private async Task<bool> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        var today = DateOnly.FromDateTime(DateTime.Today);
        Years = FinancialYear.Recent(today, 6);
        Year = Fy is { } y && Years.Contains(y) ? y : FinancialYear.LastCompleted(today);
        var from = FinancialYear.StartDate(Year);
        var to = FinancialYear.EndDate(Year);
        Rows = (await service.GetAsync(Portfolio.PortfolioId)).Select(h => FixedIncomeCalc.Year(h, from, to)).ToList();
        Accruals = await accruals.GetAsync(Portfolio.PortfolioId, Year);
        return true;
    }
}
