using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Statements;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>
/// The printable report pack for the selected portfolio (?fy=2026&amp;compilation=true&amp;signed=2026-10-15).
/// The practice details are the signed-in login's own.
/// </summary>
public class ReportPackModel(CurrentPortfolio current, FinancialStatementsService statements, ClientService clients, Core.Practice.AttachmentService attachments) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? Fy { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Compilation { get; set; } = true;

    [BindProperty(SupportsGet = true)]
    public DateOnly? Signed { get; set; }

    public int Year { get; private set; }
    public PortfolioSummary? Portfolio { get; private set; }
    public ReportPack? Pack { get; private set; }
    public IReadOnlyList<Core.Practice.Attachment> Attachments { get; private set; } = [];

    public IReadOnlyList<int> Years
    {
        get
        {
            var years = FinancialYear.Recent(StatementsModel.Today).ToList();
            if (!years.Contains(Year))
                years.Add(Year);
            return years;
        }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        Year = StatementFormat.ClampYear(Fy, FinancialYear.LastCompleted(StatementsModel.Today));
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Page();

        var fs = await statements.BuildAsync(Portfolio.PortfolioId, Year);
        if (fs is null)
            return NotFound();
        var practice = await clients.GetPracticeAsync(CurrentPortfolio.UserId(User));
        var signed = Signed is { Year: >= 1900 and <= 2100 } d ? d : (DateOnly?)null;
        Pack = ReportPackBuilder.Build(fs, practice, new ReportPackOptions(Compilation, signed));
        Attachments = await attachments.ListAsync(Portfolio.PortfolioId, year: Year);
        return Page();
    }
}
