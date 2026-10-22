using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Bills;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.Practice;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Bills;

/// <summary>One bill or supplier credit: its lines, payments, credits applied, files and actions.</summary>
public class ViewModel(CurrentPortfolio current, BillService bills, ChartOfAccountsService chart, DocumentLists lists, AttachmentService attachments,
    ILogger<ViewModel> logger) : BooksPageModel(current, logger)
{
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    [BindProperty]
    public InvoicePaymentInput Payment { get; set; } = new();

    [BindProperty]
    public RepeatingInvoiceInput Repeat { get; set; } = new();

    public BillDetail? Bill { get; private set; }
    public IReadOnlyList<AccountChoice> BankAccounts { get; private set; } = [];
    public DocumentListSet Lists { get; private set; } = DocumentLists.Empty;
    public IReadOnlyList<BillSummary> OpenBills { get; private set; } = [];
    public IReadOnlyList<Attachment> Files { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
            return RedirectToPage("/Bills/Index");
        Payment = new InvoicePaymentInput { Date = DateTime.Today, Amount = decimal.Round(Bill!.Balance, 2), AccountId = BankAccounts.FirstOrDefault()?.AccountId ?? 0 };
        return Page();
    }

    public async Task<IActionResult> OnPostPaymentAsync()
    {
        if (!await LoadAsync())
            return RedirectToPage("/Bills/Index");
        return Done(await RunAsync(() => bills.AddPaymentAsync(PortfolioId, Id, Payment)), "Payment recorded and taken out of the bank account.", new { id = Id });
    }

    public async Task<IActionResult> OnPostDeletePaymentAsync(int paymentId)
    {
        if (!await LoadAsync())
            return RedirectToPage("/Bills/Index");
        return Done(await RunAsync(() => bills.DeletePaymentAsync(PortfolioId, paymentId)), "Payment deleted.", new { id = Id });
    }

    public async Task<IActionResult> OnPostApplyAsync(int billId, decimal amount, DateTime? date)
    {
        if (!await LoadAsync())
            return RedirectToPage("/Bills/Index");
        return Done(await RunAsync(() => bills.ApplyCreditAsync(PortfolioId, Id, billId, DateOnly.FromDateTime(date ?? DateTime.Today), amount)),
            "Credit applied to the bill.", new { id = Id });
    }

    public async Task<IActionResult> OnPostRepeatAsync()
    {
        if (!await LoadAsync())
            return RedirectToPage("/Bills/Index");
        if (Bill!.Currency is not null)
            return Done(Core.Data.ProcResult.Failed("Only a bill in " + Core.Regions.RegionSettings.Current.Currency + " can repeat."), "", new { id = Id });
        var start = RepeatFrequencies.Occurrence(Bill.IssueDate, Repeat.Frequency, 1);
        var input = BillInput.From(Bill);
        input.Number = null;
        return Done(await RunAsync(() => bills.SaveRepeatingAsync(PortfolioId, 0, input, start, Repeat)),
            $"It will repeat from {start:d}. Create them on Business > Repeating bills.", new { id = Id });
    }

    public async Task<IActionResult> OnPostVoidAsync()
    {
        if (!await LoadAsync())
            return RedirectToPage("/Bills/Index");
        var draft = Bill!.IsDraft;
        var result = await RunAsync(() => bills.VoidBillAsync(PortfolioId, Id));
        if (!result.Ok)
            return Done(result, "", new { id = Id });
        TempData["Message"] = draft ? "Draft deleted." : "Bill voided and its journal removed.";
        return draft ? RedirectToPage("/Bills/Index") : RedirectToPage(new { id = Id });
    }

    private async Task<bool> LoadAsync()
    {
        if (!await LoadPortfolioAsync())
            return false;
        Bill = await bills.GetBillAsync(PortfolioId, Id);
        if (Bill is null)
        {
            TempData["Error"] = "That bill wasn't found in this portfolio.";
            return false;
        }
        BankAccounts = await chart.GetImportAccountsAsync(PortfolioId);
        Lists = await lists.LoadAsync(PortfolioId, purchases: true);
        if (Bill.IsCredit && Bill.Balance > 0 && Bill.Currency is null)
            OpenBills = (await bills.GetBillsAsync(PortfolioId))
                .Where(b => b.SupplierId == Bill.SupplierId && !b.IsCredit && b.Balance > 0 && b.Currency is null).ToList();
        try { Files = await attachments.ListAsync(PortfolioId, AttachmentKinds.Bill, Id); }
        catch (Microsoft.Data.SqlClient.SqlException) { Files = []; }
        return true;
    }
}
