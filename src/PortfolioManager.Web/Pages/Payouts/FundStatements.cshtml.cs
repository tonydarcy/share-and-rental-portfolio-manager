using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Holdings;
using PortfolioManager.Core.Imports;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Payouts;

/// <summary>ETF and managed fund annual tax statements (AMMA / AMIT), entered once a year per fund or read from a file.</summary>
[RequestSizeLimit(CsvUpload.RequestLimit)]
[RequestFormLimits(MultipartBodyLengthLimit = CsvUpload.RequestLimit)]
public class FundStatementsModel(CurrentPortfolio current, FundTaxStatementService statements, HoldingsService holdings) : PageModel
{
    public sealed class StatementForm
    {
        public int Id { get; set; }
        /// <summary>"exchange:code".</summary>
        public string? Fund { get; set; }
        public int Year { get; set; }
        public decimal? CashDistribution { get; set; }
        public decimal? Franked { get; set; }
        public decimal? FrankingCredits { get; set; }
        public decimal? Unfranked { get; set; }
        public decimal? Interest { get; set; }
        public decimal? OtherIncome { get; set; }
        public decimal? Ncmi { get; set; }
        public decimal? DiscountedGain { get; set; }
        public decimal? OtherGain { get; set; }
        public decimal? ForeignIncome { get; set; }
        public decimal? ForeignTaxCredits { get; set; }
        public decimal? TfnWithheld { get; set; }
        public decimal? CostBaseIncrease { get; set; }
        public decimal? CostBaseDecrease { get; set; }
        public string? Comment { get; set; }
    }

    [BindProperty] public StatementForm Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public int? Edit { get; set; }
    [TempData] public string? Success { get; set; }
    [BindProperty] public IFormFile? Upload { get; set; }
    [BindProperty] public string? Data { get; set; }
    public ImportPreview<FundTaxStatement>? Preview { get; private set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<FundTaxStatement> Statements { get; private set; } = [];
    public IReadOnlyList<string> Funds { get; private set; } = [];
    public IReadOnlyList<int> Years { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (!await LoadAsync())
            return;
        if (Edit is { } id && Statements.FirstOrDefault(s => s.Id == id) is { } s)
            Input = new StatementForm
            {
                Id = s.Id, Fund = $"{s.ExchangeCode}:{s.StockCode}", Year = s.Year, CashDistribution = s.CashDistribution, Franked = s.Franked,
                FrankingCredits = s.FrankingCredits, Unfranked = s.Unfranked, Interest = s.Interest, OtherIncome = s.OtherIncome, Ncmi = s.Ncmi,
                DiscountedGain = s.DiscountedGain, OtherGain = s.OtherGain, ForeignIncome = s.ForeignIncome, ForeignTaxCredits = s.ForeignTaxCredits,
                TfnWithheld = s.TfnWithheld, CostBaseIncrease = s.CostBaseIncrease, CostBaseDecrease = s.CostBaseDecrease, Comment = s.Comment,
            };
        else
            Input.Year = FinancialYear.LastCompleted(DateOnly.FromDateTime(DateTime.Today)); // statements come after the year ends
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!await LoadAsync())
            return Page();
        if (Input.Fund?.Split(':', 2) is not [var ex, var code] || !Funds.Contains($"{ex}:{code}", StringComparer.OrdinalIgnoreCase))
        {
            ModelState.AddModelError("Input.Fund", "Pick the fund.");
            return Page();
        }
        var statement = ToStatement(ex, code);
        foreach (var e in FundTaxStatements.Validate(statement))
            ModelState.AddModelError(string.Empty, e);
        if (!ModelState.IsValid)
            return Page();
        var result = await statements.SaveAsync(Portfolio!.PortfolioId, statement);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }
        Success = $"The {FinancialYear.Label(statement.Year)} statement for {code.ToUpperInvariant()} is saved."
            + (statement.Difference is var d && Math.Abs(d) >= 1 ? $" Its parts are {Math.Abs(d):C} {(d > 0 ? "more" : "less")} than the cash distribution: check the figures." : "");
        return RedirectToPage();
    }

    /// <summary>Reads a statement file and shows what would be saved.</summary>
    public async Task<IActionResult> OnPostPreviewAsync()
    {
        if (!await LoadAsync())
            return Page();
        var (text, error) = await CsvUpload.ReadAsync(Upload);
        if (error is not null)
        {
            ModelState.AddModelError(nameof(Upload), error);
            return Page();
        }
        Preview = Check(FundStatementCsv.Parse(text!));
        Data = CsvUpload.Encode(text!);
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync()
    {
        if (!await LoadAsync())
            return Page();
        if (CsvUpload.Decode(Data) is not { } text)
        {
            ModelState.AddModelError(nameof(Upload), "The preview couldn't be read back. Choose the file again.");
            return Page();
        }
        Preview = Check(FundStatementCsv.Parse(text));
        if (Preview.FileError is not null || Preview.ValidCount == 0)
        {
            ModelState.AddModelError(nameof(Upload), "There are no statements to save.");
            return Page();
        }
        var (added, replaced, failed) = await statements.ImportAsync(Portfolio!.PortfolioId, Preview.ValidRows.ToList());
        if (failed is not null)
        {
            ModelState.AddModelError(nameof(Upload), $"Nothing was saved. {failed}");
            return Page();
        }
        Success = $"Saved {added:N0} new statement{(added == 1 ? "" : "s")}" + (replaced > 0 ? $" and replaced {replaced:N0}" : "") + "."
            + (Preview.ErrorCount > 0 ? $" {Preview.ErrorCount:N0} line{(Preview.ErrorCount == 1 ? "" : "s")} with problems {(Preview.ErrorCount == 1 ? "was" : "were")} skipped." : "");
        return RedirectToPage();
    }

    // Only funds this portfolio holds or has held
    private ImportPreview<FundTaxStatement> Check(ImportPreview<FundTaxStatement> preview) =>
        preview with
        {
            Lines = preview.Lines.Select(l => l.Value is { } s && !Funds.Contains($"{s.ExchangeCode}:{s.StockCode}", StringComparer.OrdinalIgnoreCase)
                ? l with { Value = null, Errors = [.. l.Errors, $"{s.ExchangeCode}:{s.StockCode} isn't in this portfolio."] }
                : l).ToList(),
        };

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (await current.GetSelectedAsync() is not { } p)
            return RedirectToPage();
        var result = await statements.DeleteAsync(p.PortfolioId, id);
        Success = result.Ok ? "Statement deleted." : result.Error;
        return RedirectToPage();
    }

    private FundTaxStatement ToStatement(string exchange, string code)
    {
        static decimal M(decimal? v) => Math.Round(v ?? 0, 2, MidpointRounding.AwayFromZero);
        var i = Input;
        return new FundTaxStatement(i.Id, exchange.Trim().ToUpperInvariant(), code.Trim().ToUpperInvariant(), i.Year, FinancialYear.EndDate(i.Year),
            M(i.CashDistribution), M(i.Franked), M(i.FrankingCredits), M(i.Unfranked), M(i.Interest), M(i.OtherIncome), M(i.Ncmi),
            M(i.DiscountedGain), M(i.OtherGain), M(i.ForeignIncome), M(i.ForeignTaxCredits), M(i.TfnWithheld),
            M(i.CostBaseIncrease), M(i.CostBaseDecrease), i.Comment);
    }

    private async Task<bool> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        Statements = await statements.GetAsync(Portfolio.PortfolioId);
        Funds = (await holdings.GetHoldingsAsync(Portfolio.PortfolioId, null, false)).Holdings
            .Select(h => $"{h.ExchangeCode.Trim().ToUpperInvariant()}:{h.StockCode.Trim().ToUpperInvariant()}")
            .Distinct().OrderBy(f => f[(f.IndexOf(':') + 1)..]).ToList();
        Years = FinancialYear.Recent(DateOnly.FromDateTime(DateTime.Today), 6);
        return true;
    }
}
