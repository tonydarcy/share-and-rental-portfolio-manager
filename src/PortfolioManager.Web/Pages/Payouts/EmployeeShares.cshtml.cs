using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Payouts;

/// <summary>Employee share scheme statements, one per employer per year, for item 12 of an individual return.</summary>
public class EmployeeSharesModel(CurrentPortfolio current, EssStatementService statements) : PageModel
{
    public sealed class EssForm
    {
        public int Id { get; set; }
        public int Year { get; set; }
        public string? Employer { get; set; }
        public decimal? UpfrontEligible { get; set; }
        public decimal? UpfrontNotEligible { get; set; }
        public decimal? Deferral { get; set; }
        public decimal? Pre2009 { get; set; }
        public decimal? ForeignSource { get; set; }
        public decimal? TfnWithheld { get; set; }
        public string? Comment { get; set; }
    }

    [BindProperty] public EssForm Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public int? Edit { get; set; }
    [TempData] public string? Success { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<EssStatement> Statements { get; private set; } = [];
    public IReadOnlyList<int> Years { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (!await LoadAsync())
            return;
        if (Edit is { } id && Statements.FirstOrDefault(s => s.Id == id) is { } s)
            Input = new EssForm
            {
                Id = s.Id, Year = s.Year, Employer = s.Employer, UpfrontEligible = s.UpfrontEligible, UpfrontNotEligible = s.UpfrontNotEligible,
                Deferral = s.Deferral, Pre2009 = s.Pre2009, ForeignSource = s.ForeignSource, TfnWithheld = s.TfnWithheld, Comment = s.Comment,
            };
        else
            Input.Year = FinancialYear.LastCompleted(DateOnly.FromDateTime(DateTime.Today)); // statements come after the year ends
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!await LoadAsync())
            return Page();
        static decimal M(decimal? v) => Math.Round(v ?? 0, 2, MidpointRounding.AwayFromZero);
        var i = Input;
        var statement = new EssStatement(i.Id, i.Year, i.Employer?.Trim() ?? "", M(i.UpfrontEligible), M(i.UpfrontNotEligible), M(i.Deferral),
            M(i.Pre2009), M(i.ForeignSource), M(i.TfnWithheld), i.Comment);
        foreach (var e in EssStatements.Validate(statement))
            ModelState.AddModelError(string.Empty, e);
        if (!ModelState.IsValid)
            return Page();
        // Saved to the selected portfolio only; the proc checks an edited statement belongs to it
        var result = await statements.SaveAsync(Portfolio!.PortfolioId, statement);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }
        Success = $"The {FinancialYear.Label(statement.Year)} ESS statement from {statement.Employer} is saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (await current.GetSelectedAsync() is not { } p)
            return RedirectToPage();
        var result = await statements.DeleteAsync(p.PortfolioId, id);
        Success = result.Ok ? "Statement deleted." : result.Error;
        return RedirectToPage();
    }

    private async Task<bool> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        Statements = await statements.GetAsync(Portfolio.PortfolioId);
        Years = FinancialYear.Recent(DateOnly.FromDateTime(DateTime.Today), 6);
        return true;
    }
}
