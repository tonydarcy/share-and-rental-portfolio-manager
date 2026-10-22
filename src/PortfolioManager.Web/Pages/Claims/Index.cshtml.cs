using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Claims;
using PortfolioManager.Core.Practice;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Claims;

/// <summary>
/// Expense claims: someone paid for something themselves and wants it back. Submit it with a photo of the receipt,
/// approve it (posts the expense, owed to them), then pay it back from the bank.
/// </summary>
public class IndexModel(CurrentPortfolio current, ExpenseClaimService claims, DocumentLists lists, ChartOfAccountsService chart,
    AttachmentService attachments, ILogger<IndexModel> logger) : BooksPageModel(current, logger)
{
    [BindProperty(SupportsGet = true)]
    public int? Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Show { get; set; } = "Open";

    public static readonly IReadOnlyList<string> Filters = ["Open", ClaimStatus.Submitted, ClaimStatus.Approved, ClaimStatus.Paid, ClaimStatus.Rejected, "All"];

    [BindProperty]
    public ClaimInput Input { get; set; } = new();

    [BindProperty]
    public IFormFile? Receipt { get; set; }

    public IReadOnlyList<ExpenseClaim> All { get; private set; } = [];
    public IReadOnlyList<ExpenseClaim> Claims { get; private set; } = [];
    public IReadOnlyDictionary<int, int> FileCounts { get; private set; } = new Dictionary<int, int>();
    public DocumentListSet Lists { get; private set; } = DocumentLists.Empty;
    public IReadOnlyList<AccountChoice> BankAccounts { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
            return Page();
        if (Id is { } id && All.FirstOrDefault(c => c.Id == id) is { } claim)
            Input = ClaimInput.From(claim);
        else
            Input = new ClaimInput
            {
                Date = DateTime.Today, Claimant = User.Identity?.Name,
                AccountId = Lists.Accounts.FirstOrDefault(a => a.Type == "Expense")?.AccountId ?? 0,
            };
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        if (Receipt is { Length: > 0 } && AttachmentRules.Validate(Receipt.FileName, Receipt.Length) is { } fileError)
        {
            ModelState.AddModelError(string.Empty, fileError);
            return Page();
        }
        var result = await RunAsync(() => claims.SaveAsync(PortfolioId, Id, CurrentPortfolio.UserId(User), Input, Lists.AccountIds));
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }
        var message = Id is null ? "Claim submitted for approval." : "Claim saved.";
        if (Receipt is { Length: > 0 })
        {
            using var ms = new MemoryStream();
            await Receipt.CopyToAsync(ms);
            var file = await attachments.AddAsync(PortfolioId, CurrentPortfolio.UserId(User), AttachmentKinds.ExpenseClaim, result.Id, null,
                Receipt.FileName, ms.ToArray(), "Receipt");
            message += file.Ok ? " Receipt attached." : $" The receipt wasn't attached: {file.Error}";
        }
        TempData["Message"] = message;
        return RedirectToPage(new { id = (int?)null });
    }

    public async Task<IActionResult> OnPostApproveAsync(int claimId) =>
        await LoadAsync()
            ? Done(await RunAsync(() => claims.ApproveAsync(PortfolioId, claimId, CurrentPortfolio.UserId(User))), "Claim approved and posted.", new { show = Show, id = (int?)null })
            : Forbid();

    public async Task<IActionResult> OnPostRejectAsync(int claimId, string? reason) =>
        await LoadAsync()
            ? Done(await RunAsync(() => claims.RejectAsync(PortfolioId, claimId, reason)), "Claim rejected.", new { show = Show, id = (int?)null })
            : Forbid();

    public async Task<IActionResult> OnPostUnapproveAsync(int claimId) =>
        await LoadAsync()
            ? Done(await RunAsync(() => claims.UnapproveAsync(PortfolioId, claimId)), "Approval taken back and its journal removed.", new { show = Show, id = (int?)null })
            : Forbid();

    public async Task<IActionResult> OnPostDeleteAsync(int claimId) =>
        await LoadAsync()
            ? Done(await RunAsync(() => claims.DeleteAsync(PortfolioId, claimId)), "Claim deleted.", new { show = Show, id = (int?)null })
            : Forbid();

    public async Task<IActionResult> OnPostPayAsync(List<int> pay, DateTime? payDate, int bankAccountId) =>
        await LoadAsync()
            ? Done(await RunAsync(() => claims.PayAsync(PortfolioId, pay, DateOnly.FromDateTime(payDate ?? DateTime.Today), bankAccountId)),
                "Paid back and posted from the bank account.", new { show = Show, id = (int?)null })
            : Forbid();

    public async Task<IActionResult> OnPostUndoPaymentAsync(int journalId) =>
        await LoadAsync()
            ? Done(await RunAsync(() => claims.UndoPaymentAsync(PortfolioId, journalId)), "Payment undone. The claims are approved and waiting to be paid again.", new { show = Show, id = (int?)null })
            : Forbid();

    private async Task<bool> LoadAsync()
    {
        if (!await LoadPortfolioAsync())
            return false;
        All = await claims.GetAsync(PortfolioId);
        Claims = Show switch
        {
            "All" => All,
            "Open" => All.Where(c => c.Status is ClaimStatus.Submitted or ClaimStatus.Approved).ToList(),
            _ => All.Where(c => c.Status == Show).ToList(),
        };
        Lists = await lists.LoadAsync(PortfolioId, purchases: true);
        BankAccounts = await chart.GetImportAccountsAsync(PortfolioId);
        try { FileCounts = await attachments.CountsAsync(PortfolioId, AttachmentKinds.ExpenseClaim); }
        catch (Microsoft.Data.SqlClient.SqlException) { FileCounts = new Dictionary<int, int>(); }
        return true;
    }
}
