using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Email;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Practice;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Clients;

/// <summary>
/// Questions about transactions for the client to answer online. Anyone who can open the portfolio, read-only
/// included, can answer; asking, closing and deleting need edit access.
/// </summary>
[AllowViewOnly]
public class QueriesModel(CurrentPortfolio current, ClientQueryService queries, JournalService journals, BankImportService imports,
    IEmailSender email, SiteBranding site, ILogger<QueriesModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public int? Journal { get; set; }
    [BindProperty(SupportsGet = true)] public int? Line { get; set; }
    [BindProperty] public string? Question { get; set; }
    [BindProperty] public string? Answer { get; set; }
    [BindProperty] public string? EmailTo { get; set; }
    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<ClientQuery> Queries { get; private set; } = [];
    public (DateOnly Date, decimal Amount, string Text)? About { get; private set; }
    public bool EmailOn => email.IsConfigured;

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        Queries = await queries.ListAsync(Portfolio.PortfolioId);
        if (Portfolio.CanEdit)
            About = await AboutAsync(Portfolio.PortfolioId);
    }

    public async Task<IActionResult> OnPostAskAsync()
    {
        if (await current.GetSelectedAsync() is not { CanEdit: true } p)
            return Forbid();
        var about = await AboutAsync(p.PortfolioId);
        var r = await queries.AskAsync(p.PortfolioId, CurrentPortfolio.UserId(User), Question ?? "", about is null ? null : Journal, about is null ? null : Line,
            about?.Date, about?.Amount, about?.Text);
        if (!r.Ok)
        {
            Error = r.Error;
            return RedirectToPage(new { journal = Journal, line = Line });
        }
        Message = "Question saved. The client sees it on Clients > Client queries.";
        if (!string.IsNullOrWhiteSpace(EmailTo) && email.IsConfigured)
        {
            try
            {
                await email.SendAsync(EmailTemplates.Simple(EmailTo.Trim(), $"A question about {p.Name}",
                    [$"{User.Identity?.Name} has a question about a transaction in {p.Name}:", Question!.Trim(),
                     "Sign in to answer it online."], "Answer the question", site.Link(Request, Url.Page("/Clients/Queries")!), User.Identity?.Name));
                Message += $" We've emailed {EmailTo.Trim()}.";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Couldn't email a client query for portfolio {PortfolioId}", p.PortfolioId);
                Error = "The question is saved, but the email couldn't be sent.";
            }
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAnswerAsync(int id)
    {
        if (await current.GetSelectedAsync() is not { } p)
            return Forbid();
        var r = await queries.AnswerAsync(p.PortfolioId, id, Answer ?? "", CurrentPortfolio.UserId(User));
        if (r.Ok) Message = "Thanks, your answer is saved."; else Error = r.Error;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostStatusAsync(int id, bool closed)
    {
        if (await current.GetSelectedAsync() is not { CanEdit: true } p)
            return Forbid();
        var r = await queries.SetStatusAsync(p.PortfolioId, id, closed);
        if (r.Ok) Message = closed ? "Closed." : "Opened again."; else Error = r.Error;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (await current.GetSelectedAsync() is not { CanEdit: true } p)
            return Forbid();
        var r = await queries.DeleteAsync(p.PortfolioId, id);
        if (r.Ok) Message = "Deleted."; else Error = r.Error;
        return RedirectToPage();
    }

    /// <summary>The journal or unallocated bank line the question is about, only when it is in the portfolio.</summary>
    private async Task<(DateOnly Date, decimal Amount, string Text)?> AboutAsync(int portfolioId)
    {
        if (Journal is { } j && await journals.GetAsync(portfolioId, j) is { } journal)
            return (journal.Date, journal.TotalDebit, journal.Description);
        if (Line is { } l && await imports.GetLineAsync(portfolioId, l) is { } line)
            return (line.Date, line.Amount, line.Description);
        return null;
    }
}
