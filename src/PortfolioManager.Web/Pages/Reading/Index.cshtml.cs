using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Bills;
using PortfolioManager.Core.Claims;
using PortfolioManager.Core.Practice;
using PortfolioManager.Core.Reading;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Reading;

/// <summary>
/// Upload a receipt, bill, statement or contract note: it is read and turned into a draft bill, an expense claim or a
/// filled-in trade form, with the file attached. Nothing is approved or posted until the person checks it and saves.
/// </summary>
public class IndexModel(CurrentPortfolio current, DocumentReadingService reader, BillService bills, ExpenseClaimService claims,
    DocumentLists lists, AttachmentService attachments, ILogger<IndexModel> logger) : BooksPageModel(current, logger)
{
    public static readonly IReadOnlyList<(string Value, string Label)> Makes =
    [
        ("Bill", "A draft bill from a supplier"),
        ("Claim", "An expense claim (I paid for it myself)"),
        ("Trade", "A share trade (contract note)"),
        ("Show", "Just show me what it says"),
    ];

    [BindProperty]
    public IFormFile? Upload { get; set; }

    [BindProperty]
    public string Make { get; set; } = "Bill";

    public bool UsesClaude => reader.UsesClaude;
    public ReadResult? Result { get; private set; }

    public async Task OnGetAsync() => await LoadPortfolioAsync();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!await LoadPortfolioAsync())
            return Forbid();
        if (Upload is not { Length: > 0 })
        {
            ModelState.AddModelError(string.Empty, "Choose the PDF or photo to read.");
            return Page();
        }
        var contentType = AttachmentRules.ContentTypeFor(Upload.FileName) ?? "";
        using var ms = new MemoryStream();
        await Upload.CopyToAsync(ms, ct);
        var data = ms.ToArray();
        var (read, error) = await reader.ReadAsync(data, contentType, CultureInfo.CurrentCulture, ct);
        if (read is null)
        {
            ModelState.AddModelError(string.Empty, error!);
            return Page();
        }
        Result = read;
        if (Make == "Show" || !CanEdit)
            return Page();
        if (Make == "Trade")
            return RedirectToPage("/Trades/Add", new
            {
                code = read.TradeCode, type = read.TradeSide, qty = read.Units, price = read.Price, brokerage = read.Brokerage,
                date = read.Date?.ToString("yyyy-MM-dd"),
            });
        if (read.Total is not > 0)
        {
            ModelState.AddModelError(string.Empty, "No total could be read, so nothing was made. Enter it by hand.");
            return Page();
        }

        var set = await lists.LoadAsync(PortfolioId, purchases: true);
        var expense = set.Accounts.FirstOrDefault(a => a.Type == "Expense")?.AccountId ?? 0;
        if (Make == "Claim")
        {
            var input = ReadingDrafts.Claim(read, User.Identity?.Name ?? "Me", expense, ReadingDrafts.TaxCodeFor(read, set.TaxCodes, null), DateTime.Today);
            var saved = await RunAsync(() => claims.SaveAsync(PortfolioId, null, CurrentPortfolio.UserId(User), input, set.AccountIds));
            if (!saved.Ok)
                return Failed(saved.Error!);
            await AttachAsync(AttachmentKinds.ExpenseClaim, saved.Id, data);
            TempData["Message"] = "Expense claim made from the receipt and the receipt attached. Check it, then change it here if anything is wrong.";
            return RedirectToPage("/Claims/Index", new { id = saved.Id });
        }

        var supplier = ReadingDrafts.MatchSupplier(await bills.GetSuppliersAsync(PortfolioId), read);
        var supplierId = supplier?.SupplierId;
        if (supplierId is null)
        {
            var added = await RunAsync(() => bills.SaveSupplierAsync(PortfolioId, new SupplierInput
            {
                Name = read.Payee is { Length: > 0 and <= 200 } p ? p : "New supplier (from a scanned bill)",
                TaxNumber = read.TaxNumber is { Length: <= 50 } t ? t : null,
            }));
            if (!added.Ok)
                return Failed(added.Error!);
            supplierId = added.Id;
        }
        var bill = ReadingDrafts.Bill(read, supplierId!.Value, supplier?.DueDays ?? 30, supplier?.DefaultAccountId ?? expense,
            ReadingDrafts.TaxCodeFor(read, set.TaxCodes, supplier?.DefaultTaxCodeId), DateTime.Today, Core.Regions.RegionSettings.Current.Currency);
        if (bill.Currency is not null)
            bill.ExchangeRate = null;
        var result = await RunAsync(() => bills.SaveBillAsync(PortfolioId, null, bill, false, set.AccountIds));
        if (!result.Ok && bill.Currency is not null)
        {
            // No rate was read for a foreign bill: keep it in the portfolio's currency and say so
            bill.Currency = null;
            bill.Notes += $" It was in {read.Currency}: set the currency and rate.";
            result = await RunAsync(() => bills.SaveBillAsync(PortfolioId, null, bill, false, set.AccountIds));
        }
        if (!result.Ok)
            return Failed(result.Error!);
        await AttachAsync(AttachmentKinds.Bill, result.Id, data);
        TempData["Message"] = (supplier is null ? "A new supplier was added and a" : "A") +
            " draft bill made from the document, with the file attached. Check the supplier, account and amounts, then approve it.";
        return RedirectToPage("/Bills/Edit", new { id = result.Id });
    }

    private IActionResult Failed(string error)
    {
        ModelState.AddModelError(string.Empty, error);
        return Page();
    }

    private async Task AttachAsync(string kind, int? id, byte[] data)
    {
        var file = await attachments.AddAsync(PortfolioId, CurrentPortfolio.UserId(User), kind, id, null, Upload!.FileName, data, "Read on upload");
        if (!file.Ok)
            TempData["Error"] = $"The file wasn't attached: {file.Error}";
    }
}
