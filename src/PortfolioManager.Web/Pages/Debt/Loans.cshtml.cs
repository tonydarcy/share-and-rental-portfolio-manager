using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Debt;
using PortfolioManager.Core.Holdings;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Debt;

/// <summary>Margin and investment loans: each loan's entries, the year's interest and deductible part, and the loan-to-value ratio.</summary>
public class LoansModel(CurrentPortfolio current, LoanService loans, HoldingsService holdings) : PageModel
{
    public sealed class LoanForm
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Lender { get; set; }
        public string Kind { get; set; } = LoanKinds.Margin;
        public decimal? RatePercent { get; set; }
        public decimal? DeductiblePercent { get; set; } = 100;
        public decimal? Limit { get; set; }
        public DateOnly? Opened { get; set; }
        public string? Comment { get; set; }
    }

    public sealed class EntryForm
    {
        public int LoanId { get; set; }
        public DateOnly? Date { get; set; }
        public string Kind { get; set; } = LoanEntryKinds.Interest;
        public decimal? Amount { get; set; }
        public string? Comment { get; set; }
    }

    [BindProperty] public LoanForm Input { get; set; } = new();
    [BindProperty] public EntryForm Entry { get; set; } = new();
    [BindProperty(SupportsGet = true)] public int? Fy { get; set; }
    [BindProperty(SupportsGet = true)] public int? Edit { get; set; }
    [TempData] public string? Message { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public int Year { get; private set; }
    public IReadOnlyList<int> Years { get; private set; } = [];
    public LoanReport Report { get; private set; } = new([], 0);
    public LoanReport Today { get; private set; } = new([], 0);
    public IReadOnlyList<LoanEntry> Entries { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (!await LoadAsync())
            return;
        if (Edit is { } id && Report.Loans.FirstOrDefault(l => l.Loan.Id == id)?.Loan is { } l)
            Input = new LoanForm
            {
                Id = l.Id, Name = l.Name, Lender = l.Lender, Kind = l.Kind, RatePercent = l.RatePercent, DeductiblePercent = l.DeductiblePercent,
                Limit = l.Limit, Opened = l.Opened, Comment = l.Comment,
            };
        Entry.Date = DateOnly.FromDateTime(DateTime.Today);
        Entry.LoanId = Report.Loans.FirstOrDefault()?.Loan.Id ?? 0;
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!await LoadAsync())
            return Page();
        var i = Input;
        var loan = new Loan(i.Id, i.Name?.Trim() ?? "", i.Lender, i.Kind, i.RatePercent ?? 0, i.DeductiblePercent ?? 100, i.Limit,
            i.Opened ?? DateOnly.FromDateTime(DateTime.Today), i.Comment);
        foreach (var e in LoanCalc.Validate(loan))
            ModelState.AddModelError(string.Empty, e);
        if (!ModelState.IsValid)
            return Page();
        // The proc checks an edited loan belongs to the selected portfolio
        return Done(await loans.SaveAsync(Portfolio!.PortfolioId, loan), $"{loan.Name} is saved.");
    }

    public async Task<IActionResult> OnPostEntryAsync()
    {
        if (!await LoadAsync())
            return Page();
        if (Entry.Date is not { } date || Entry.Amount is not > 0 || !LoanEntryKinds.All.Contains(Entry.Kind))
        {
            ModelState.AddModelError(string.Empty, "Enter the date, the type of entry and an amount more than nil.");
            return Page();
        }
        var entry = new LoanEntry(0, Entry.LoanId, date, Entry.Kind, Math.Round(Entry.Amount.Value, 2), Entry.Comment);
        return Done(await loans.AddEntryAsync(Portfolio!.PortfolioId, entry), $"{LoanEntryKinds.Text(entry.Kind)} of {entry.Amount:C} added.");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (await current.GetSelectedAsync() is not { } p)
            return RedirectToPage();
        return Done(await loans.DeleteAsync(p.PortfolioId, id), "Loan deleted, with its entries.");
    }

    public async Task<IActionResult> OnPostDeleteEntryAsync(int id)
    {
        if (await current.GetSelectedAsync() is not { } p)
            return RedirectToPage();
        return Done(await loans.DeleteEntryAsync(p.PortfolioId, id), "Entry deleted.");
    }

    private IActionResult Done(Core.Data.ProcResult r, string ok)
    {
        Message = r.Ok ? ok : r.Error;
        return RedirectToPage(new { fy = Fy });
    }

    private async Task<bool> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        var today = DateOnly.FromDateTime(DateTime.Today);
        Years = FinancialYear.Recent(today, 6);
        Year = Fy is { } y && Years.Contains(y) ? y : FinancialYear.EndYearFor(today);
        var (all, entries) = await loans.GetAsync(Portfolio.PortfolioId);
        Entries = entries;
        var value = (await holdings.GetHoldingsAsync(Portfolio.PortfolioId, null, true)).Total.MarketValue;
        Report = new LoanReport(all.Select(l => LoanCalc.Year(l, entries, FinancialYear.StartDate(Year), FinancialYear.EndDate(Year))).ToList(), value);
        Today = new LoanReport(all.Select(l => LoanCalc.Year(l, entries, FinancialYear.StartDate(FinancialYear.EndYearFor(today)), today)).ToList(), value);
        return true;
    }
}
