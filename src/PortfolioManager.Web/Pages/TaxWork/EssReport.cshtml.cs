using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>
/// For an employer: the register of shares, rights and options given to employees under an employee share scheme, each
/// employee's ESS statement for the year, and the year's ESS annual report figures as a CSV.
/// </summary>
public class EssReportModel(CurrentPortfolio current, EssGrantService grants) : PageModel
{
    public sealed class GrantForm
    {
        public int Id { get; set; }
        public string? Employee { get; set; }
        public string? EmployeeRef { get; set; }
        public string Scheme { get; set; } = EssSchemes.UpfrontEligible;
        public string Interest { get; set; } = "Shares";
        public DateOnly? Acquired { get; set; }
        public decimal? Interests { get; set; }
        public decimal? MarketValue { get; set; }
        public decimal? AmountPaid { get; set; }
        public decimal? ExercisePrice { get; set; }
        public DateOnly? TaxingPoint { get; set; }
        public decimal? TaxingPointValue { get; set; }
        public bool TfnQuoted { get; set; } = true;
        public decimal? TfnWithheld { get; set; }
        public bool ForeignSource { get; set; }
        public string? Comment { get; set; }
    }

    [BindProperty] public GrantForm Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public int? Fy { get; set; }
    [BindProperty(SupportsGet = true)] public int? Edit { get; set; }
    /// <summary>Shows each employee's statement, one to a page, for printing.</summary>
    [BindProperty(SupportsGet = true)] public bool Statements { get; set; }
    [TempData] public string? Success { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<EssGrant> All { get; private set; } = [];
    public EssRegister? Register { get; private set; }
    public IReadOnlyList<int> Years { get; private set; } = [];
    public bool NeedsMigration { get; private set; }
    public int Year => Fy ?? FinancialYear.LastCompleted(DateOnly.FromDateTime(DateTime.Today));

    public async Task OnGetAsync()
    {
        if (!await LoadAsync())
            return;
        if (Edit is { } id && All.FirstOrDefault(g => g.Id == id) is { } g)
            Input = new GrantForm
            {
                Id = g.Id, Employee = g.Employee, EmployeeRef = g.EmployeeRef, Scheme = g.Scheme, Interest = g.Interest, Acquired = g.Acquired,
                Interests = g.Interests, MarketValue = g.MarketValue, AmountPaid = g.AmountPaid, ExercisePrice = g.ExercisePrice,
                TaxingPoint = g.TaxingPoint, TaxingPointValue = g.TaxingPointValue, TfnQuoted = g.TfnQuoted, TfnWithheld = g.TfnWithheld,
                ForeignSource = g.ForeignSource, Comment = g.Comment,
            };
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        if (!await LoadAsync() || Register is null)
            return RedirectToPage();
        return File(System.Text.Encoding.UTF8.GetBytes(EssRegisters.ToCsv(Register)), "text/csv", $"ess-annual-report-{FinancialYear.Label(Year)}.csv");
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!await LoadAsync())
            return Page();
        var i = Input;
        if (i.Acquired is null)
            ModelState.AddModelError("Input.Acquired", "Enter the date the employee acquired them.");
        static decimal M(decimal? v) => Math.Round(v ?? 0, 2, MidpointRounding.AwayFromZero);
        var deferral = i.Scheme == EssSchemes.Deferral;
        var grant = new EssGrant(i.Id, i.Employee?.Trim() ?? "", string.IsNullOrWhiteSpace(i.EmployeeRef) ? null : i.EmployeeRef.Trim(), i.Scheme, i.Interest,
            i.Acquired ?? default, i.Interests ?? 0, M(i.MarketValue), M(i.AmountPaid), i.ExercisePrice, deferral ? i.TaxingPoint : null,
            deferral && i.TaxingPointValue is { } v ? M(v) : null, i.TfnQuoted, M(i.TfnWithheld), i.ForeignSource, i.Comment);
        foreach (var e in EssRegisters.Validate(grant))
            ModelState.AddModelError(string.Empty, e);
        if (!ModelState.IsValid)
            return Page();
        var result = await grants.SaveAsync(Portfolio!.PortfolioId, grant);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }
        Success = $"The grant to {grant.Employee} is saved." + (grant.TfnWithheld < grant.WithholdingDue
            ? $" No TFN was quoted, so {grant.WithholdingDue:C} should be withheld ({EssSchemes.NoTfnRate:P0} of the discount)." : "");
        return RedirectToPage(new { fy = grant.ReportDate is { } d ? FinancialYear.EndYearFor(d) : Year });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (await current.GetSelectedAsync() is not { } p)
            return RedirectToPage();
        var result = await grants.DeleteAsync(p.PortfolioId, id);
        Success = result.Ok ? "Grant deleted." : result.Error;
        return RedirectToPage(new { fy = Fy });
    }

    private async Task<bool> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        Years = FinancialYear.Recent(DateOnly.FromDateTime(DateTime.Today), 6);
        if (await grants.GetAsync(Portfolio.PortfolioId) is not { } all)
        {
            NeedsMigration = true;
            return false;
        }
        All = all;
        Register = EssRegisters.Build(all, Year);
        return true;
    }
}
