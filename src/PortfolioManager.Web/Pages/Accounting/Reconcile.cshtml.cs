using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>Bank reconciliation for one account (?accountId=12, optionally &amp;id= a past one or &amp;date= a new statement date).</summary>
public class ReconcileModel(CurrentPortfolio current, BankReconciliationService recs) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? AccountId { get; set; }

    [BindProperty(SupportsGet = true, Name = "id")]
    public int? ReconciliationId { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? Date { get; set; }

    [BindProperty]
    public BankRecInput Input { get; set; } = new();

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<AccountChoice> Accounts { get; private set; } = [];
    public BankRecWorksheet? Sheet { get; private set; }
    public IReadOnlyList<BankReconciliation> History { get; private set; } = [];
    public BankRecSummary? Summary { get; private set; }
    public IReadOnlyList<BankRecLine> Unpresented { get; private set; } = [];

    public bool ReadOnly => Sheet?.Current is { IsComplete: true };
    public string AccountName => Accounts.FirstOrDefault(a => a.AccountId == AccountId)?.DisplayName ?? "";

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        return Page();
    }

    public Task<IActionResult> OnPostSaveAsync() => SaveAsync(complete: false);

    public Task<IActionResult> OnPostFinishAsync() => SaveAsync(complete: true);

    public async Task<IActionResult> OnPostReopenAsync(int recId)
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        var result = await recs.ReopenAsync(Portfolio!.PortfolioId, recId);
        if (result.Ok)
            TempData["Message"] = "Reopened. Change the ticks or balance and finish it again.";
        else
            TempData["Error"] = result.Error;
        return RedirectToPage(new { accountId = AccountId, id = result.Ok ? recId : (int?)null });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int recId)
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        var result = await recs.DeleteAsync(Portfolio!.PortfolioId, recId);
        if (result.Ok)
            TempData["Message"] = "Deleted the reconciliation in progress.";
        else
            TempData["Error"] = result.Error;
        return RedirectToPage(new { accountId = AccountId });
    }

    private async Task<IActionResult> SaveAsync(bool complete)
    {
        AccountId = Input.AccountId;
        if (await LoadAsync() is { } refusal)
            return refusal;

        var result = await recs.SaveAsync(Portfolio!.PortfolioId, Input, complete);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }
        TempData["Message"] = complete
            ? $"Reconciled to {Input.StatementDate:d MMM yyyy}."
            : "Saved. Come back to it any time.";
        return RedirectToPage(new { accountId = AccountId, id = result.Id });
    }

    private async Task<IActionResult?> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return HttpMethods.IsGet(Request.Method) ? Page() : Forbid();

        Accounts = await recs.GetAccountsAsync(Portfolio.PortfolioId);
        if (AccountId is null || Accounts.All(a => a.AccountId != AccountId))
            AccountId = Accounts.FirstOrDefault()?.AccountId;
        if (AccountId is not { } accountId)
            return null;

        History = await recs.GetHistoryAsync(Portfolio.PortfolioId, accountId);
        if (ReconciliationId is { } id && History.All(r => r.ReconciliationId != id))
            ReconciliationId = null;
        Sheet = await recs.GetWorksheetAsync(Portfolio.PortfolioId, accountId, ReconciliationId, Date);

        if (HttpMethods.IsGet(Request.Method))
        {
            Input = new BankRecInput
            {
                ReconciliationId = Sheet.Current is { IsComplete: false } c ? c.ReconciliationId : null,
                AccountId = accountId,
                StatementDate = Sheet.StatementDate.ToDateTime(TimeOnly.MinValue),
                OpeningBalance = Sheet.OpeningBalance,
                StatementBalance = Sheet.Current?.StatementBalance ?? 0,
                Ticked = Sheet.Ticked.Select(l => l.LineId).ToList(),
            };
        }

        if (Sheet.Current is { IsComplete: true } done)
        {
            Unpresented = await recs.GetUnpresentedAsync(Portfolio.PortfolioId, done);
            Summary = BankRecSummary.From(done.StatementBalance, Unpresented, Sheet.LedgerBalance);
        }
        return null;
    }
}

/// <summary>Lines for the reconciliation table; Ticked is null when the table is read only.</summary>
public sealed record RecLinesModel(IReadOnlyList<BankRecLine> Lines, IReadOnlySet<int>? Ticked);
