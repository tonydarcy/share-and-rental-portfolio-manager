using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Email;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.SalesTax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Invoices;

/// <summary>One invoice as the customer sees it (print or save as PDF), with its payments and actions.</summary>
public class ViewModel(CurrentPortfolio current, InvoiceService invoices, InvoiceExtrasService extras, ClientService clients, ChartOfAccountsService chart,
    SalesTaxService salesTax, IEmailSender email, ILogger<ViewModel> logger) : PageModel
{
    public InvoiceKind Kind { get; private set; } = InvoiceKind.Plain;
    public bool HasLogo { get; private set; }

    /// <summary>For a credit note: the customer's approved invoices still owing, to apply it to.</summary>
    public IReadOnlyList<InvoiceSummary> OpenInvoices { get; private set; } = [];

    [BindProperty] public RepeatingInvoiceInput Repeat { get; set; } = new();

    public async Task<IActionResult> OnPostCreditNoteAsync()
    {
        if (!await LoadAsync())
            return RedirectToPage("/Invoices/Index");
        var income = (await chart.GetAccountsAsync(Portfolio!.PortfolioId)).Where(a => a.Type == "Revenue").Select(a => a.AccountId).ToHashSet();
        var r = await extras.CreateCreditNoteAsync(Portfolio.PortfolioId, Id, income);
        if (!r.Ok)
        {
            TempData["Error"] = r.Error;
            return RedirectToPage(new { id = Id });
        }
        TempData["Message"] = "Draft credit note made with the invoice's lines. Change the amounts to what is being credited, then approve it.";
        return RedirectToPage("/Invoices/Edit", new { id = r.Id });
    }

    public async Task<IActionResult> OnPostApplyAsync(int invoiceId, decimal amount, DateTime? date)
    {
        if (!await LoadAsync())
            return RedirectToPage("/Invoices/Index");
        var r = await Run(() => extras.ApplyCreditNoteAsync(Portfolio!.PortfolioId, Id, invoiceId, DateOnly.FromDateTime(date ?? DateTime.Today), amount));
        TempData[r.Ok ? "Message" : "Error"] = r.Ok ? "Credit note applied." : r.Error;
        return RedirectToPage(new { id = Id });
    }

    /// <summary>Keeps this invoice as a repeating one; the first new one is due one period after this invoice's date.</summary>
    public async Task<IActionResult> OnPostRepeatAsync()
    {
        if (!await LoadAsync())
            return RedirectToPage("/Invoices/Index");
        var input = InvoiceInput.From(Invoice!);
        var offset = input.DueDate - input.IssueDate;
        input.IssueDate = RepeatFrequencies.Occurrence(Invoice!.IssueDate, Repeat.Frequency, 1).ToDateTime(TimeOnly.MinValue);
        input.DueDate = input.IssueDate + offset;
        var r = await extras.SaveRepeatingAsync(Portfolio!.PortfolioId, 0, input, Repeat);
        TempData[r.Ok ? "Message" : "Error"] = r.Ok ? $"It will repeat from {input.IssueDate:d}. Create them on Invoices > Repeating invoices." : r.Error;
        return RedirectToPage(new { id = Id });
    }

    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    [BindProperty]
    public InvoicePaymentInput Payment { get; set; } = new();

    public PortfolioSummary? Portfolio { get; private set; }
    public InvoiceDetail? Invoice { get; private set; }
    public InvoiceSettings Settings { get; private set; } = InvoiceSettings.Default;
    public ClientDetails? Business { get; private set; }
    public IReadOnlyList<AccountChoice> BankAccounts { get; private set; } = [];
    public string TaxName { get; private set; } = "Tax";
    public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Today);
    public bool CanSendEmail => email.IsConfigured;

    /// <summary>The seller's name on the invoice: the client details' legal name, else the portfolio's.</summary>
    public string BusinessName => Business?.LegalName is { Length: > 0 } n ? n : Portfolio?.Name ?? "";

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
            return RedirectToPage("/Invoices/Index");
        Payment = new InvoicePaymentInput { Date = DateTime.Today, Amount = decimal.Round(Invoice!.Balance, 2), AccountId = BankAccounts.FirstOrDefault()?.AccountId ?? 0 };
        return Page();
    }

    public async Task<IActionResult> OnPostPaymentAsync()
    {
        if (!await LoadAsync())
            return RedirectToPage("/Invoices/Index");
        var result = await Run(() => invoices.AddPaymentAsync(Portfolio!.PortfolioId, Id, Payment));
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }
        TempData["Message"] = "Payment recorded and posted to the bank account.";
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeletePaymentAsync(int paymentId)
    {
        if (!await LoadAsync())
            return RedirectToPage("/Invoices/Index");
        var result = await Run(() => invoices.DeletePaymentAsync(Portfolio!.PortfolioId, paymentId));
        TempData[result.Ok ? "Message" : "Error"] = result.Ok ? "Payment deleted." : result.Error;
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostEmailAsync(string? emailTo, string? emailNote)
    {
        if (!await LoadAsync())
            return RedirectToPage("/Invoices/Index");
        emailTo = emailTo?.Trim();
        if (!email.IsConfigured || Invoice!.IsDraft || Invoice.IsVoid)
            TempData["Error"] = "Only an approved invoice can be emailed, and only when the site has email settings.";
        else if (string.IsNullOrEmpty(emailTo) || emailTo.Length > 500 || !MimeKit.MailboxAddress.TryParse(emailTo, out _))
            TempData["Error"] = "Enter the email address to send the invoice to.";
        else
        {
            try
            {
                // Replies go to whoever sent it, not the site's own address
                var replyTo = User.Identity?.Name;
                await email.SendAsync(InvoiceEmail.Build(Invoice, Settings, BusinessName, TaxName, emailTo, replyTo,
                    emailNote is { Length: > 1000 } ? emailNote[..1000] : emailNote));
                TempData["Message"] = $"Invoice {Invoice.Number} emailed to {emailTo}.";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Couldn't email invoice {InvoiceId}", Id);
                TempData["Error"] = "The email couldn't be sent. Check the address, or try again in a few minutes.";
            }
        }
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostVoidAsync()
    {
        if (!await LoadAsync())
            return RedirectToPage("/Invoices/Index");
        var draft = Invoice!.IsDraft;
        var result = await Run(() => invoices.VoidInvoiceAsync(Portfolio!.PortfolioId, Id));
        if (!result.Ok)
        {
            TempData["Error"] = result.Error;
            return RedirectToPage(new { id = Id });
        }
        TempData["Message"] = draft ? "Draft deleted." : "Invoice voided and its journal removed.";
        return draft ? RedirectToPage("/Invoices/Index") : RedirectToPage(new { id = Id });
    }

    /// <summary>A change in a locked period comes back as a message rather than an error page.</summary>
    private async Task<Core.Data.ProcResult> Run(Func<Task<Core.Data.ProcResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (LockedPeriodService.IsLockError(ex))
        {
            logger.LogInformation("Invoice {InvoiceId} change refused: period locked", Id);
            return Core.Data.ProcResult.Failed(LockedPeriodFilter.Message(ex));
        }
    }

    private async Task<bool> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        Invoice = await invoices.GetInvoiceAsync(Portfolio.PortfolioId, Id);
        if (Invoice is null)
        {
            TempData["Error"] = "That invoice wasn't found in this portfolio.";
            return false;
        }
        Settings = await invoices.GetSettingsAsync(Portfolio.PortfolioId);
        Business = await clients.GetAsync(Portfolio.PortfolioId);
        BankAccounts = await chart.GetImportAccountsAsync(Portfolio.PortfolioId);
        TaxName = (await salesTax.GetSetupAsync(Portfolio.PortfolioId))?.TaxName ?? "Tax";
        var kinds = await extras.GetKindsAsync(Portfolio.PortfolioId);
        Kind = kinds.GetValueOrDefault(Id) ?? InvoiceKind.Plain;
        HasLogo = await extras.GetLogoAsync(Portfolio.PortfolioId) is not null;
        if (Kind.IsCreditNote && Invoice.Balance > 0)
            OpenInvoices = (await invoices.GetInvoicesAsync(Portfolio.PortfolioId))
                .Where(i => i.CustomerId == Invoice.CustomerId && i.Balance > 0 && !kinds.ContainsKey(i.InvoiceId)).ToList();
        return true;
    }
}
