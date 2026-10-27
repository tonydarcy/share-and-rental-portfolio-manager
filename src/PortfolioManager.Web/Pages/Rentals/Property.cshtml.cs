using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Debt;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Practice;
using PortfolioManager.Core.Rentals;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Rentals;

/// <summary>
/// One rental property: its details and owners, tenancies and the rent ledger, bills to the tenant, the maintenance log,
/// inspections, compliance reminders, documents, loans and the sale.
/// </summary>
public class PropertyModel(CurrentPortfolio current, RentalService rentals, PropertyService book, AttachmentService attachments, LoanService loans,
    Core.Email.IEmailSender email, ILogger<PropertyModel> logger) : PageModel
{
    public bool CanEmail => email.IsConfigured;

    /// <summary>Emails a receipt for one rent payment to the tenant.</summary>
    public async Task<IActionResult> OnPostEmailReceiptAsync(int itemId)
    {
        if (await LoadAsync() is { } other)
            return other;
        if (Book.Payments.FirstOrDefault(x => x.Id == itemId) is not { } pay || Book.Tenancies.FirstOrDefault(t => t.Id == pay.TenancyId) is not { } t)
            return Back("tenancies", ProcResult.Failed("That payment was not found."), "");
        var today = DateOnly.FromDateTime(DateTime.Today);
        return await SendAsync(t, () => TenantEmail.Receipt(t, pay, RentLedger.Position(t, Book.Payments, today), Property!.Address, Portfolio!.Name, User.Identity?.Name),
            $"Receipt emailed to {t.Email}.");
    }

    /// <summary>Emails the tenant a reminder of the rent owing today.</summary>
    public async Task<IActionResult> OnPostEmailReminderAsync(int itemId, string? note)
    {
        if (await LoadAsync() is { } other)
            return other;
        if (Book.Tenancies.FirstOrDefault(x => x.Id == itemId) is not { } t)
            return Back("tenancies", ProcResult.Failed("That tenancy was not found."), "");
        var today = DateOnly.FromDateTime(DateTime.Today);
        var message = TenantEmail.Reminder(t, RentLedger.Position(t, Book.Payments, today), today, Property!.Address, Portfolio!.Name, User.Identity?.Name,
            note is { Length: > 1000 } ? note[..1000] : note);
        if (message is null)
            return Back("tenancies", ProcResult.Failed("Nothing is owing on this tenancy, so no reminder was sent."), "");
        return await SendAsync(t, () => message, $"Reminder emailed to {t.Email}.");
    }

    private async Task<IActionResult> SendAsync(Tenancy t, Func<Core.Email.EmailMessage> build, string done)
    {
        if (!email.IsConfigured)
            return Back("tenancies", ProcResult.Failed("The site has no email settings, so nothing can be emailed."), "");
        if (string.IsNullOrWhiteSpace(t.Email) || !MimeKit.MailboxAddress.TryParse(t.Email, out _))
            return Back("tenancies", ProcResult.Failed("Add the tenant's email address to the tenancy first."), "");
        try
        {
            await email.SendAsync(build());
            return Back("tenancies", ProcResult.Success(), done);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Couldn't email tenancy {TenancyId}", t.Id);
            return Back("tenancies", ProcResult.Failed("The email couldn't be sent. Check the address, or try again in a few minutes."), "");
        }
    }

    public sealed class DetailsInput
    {
        public string? PayoutCode { get; set; }
        public string? State { get; set; }
        public decimal? Value { get; set; }
        public DateOnly? Valued { get; set; }
    }

    public sealed class TenancyInput
    {
        public int Id { get; set; }
        public string? Tenants { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public DateOnly? Start { get; set; }
        public DateOnly? LeaseEnd { get; set; }
        public decimal Rent { get; set; }
        public string Frequency { get; set; } = RentFrequencies.Week;
        public decimal? Bond { get; set; }
        public string? BondLodgement { get; set; }
        public DateOnly? RentReview { get; set; }
        public DateOnly? Vacated { get; set; }
        public string? Note { get; set; }
    }

    public sealed class LineInput
    {
        public int Id { get; set; }
        public int? TenancyId { get; set; }
        public DateOnly? Date { get; set; }
        public DateOnly? Paid { get; set; }
        public decimal Amount { get; set; }
        public string? Kind { get; set; }
        public string? Description { get; set; }
        public string? Who { get; set; }
        public string? Reference { get; set; }
        public int? Months { get; set; }
        public DateOnly? Due { get; set; }
    }

    public sealed class SaleInput
    {
        public DateOnly? Acquired { get; set; }
        public DateOnly? Contract { get; set; }
        public decimal Proceeds { get; set; }
        public decimal PurchasePrice { get; set; }
        public decimal AcquisitionCosts { get; set; }
        public decimal HoldingCosts { get; set; }
        public decimal Improvements { get; set; }
        public decimal TitleCosts { get; set; }
        public int DaysMainResidence { get; set; }
        public int DaysAbsence { get; set; }
        public DateOnly? FirstRented { get; set; }
        public decimal? MarketValueFirstRented { get; set; }
        public string? Note { get; set; }
    }

    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty] public DetailsInput Details { get; set; } = new();
    [BindProperty] public TenancyInput TenancyForm { get; set; } = new();
    [BindProperty] public LineInput Line { get; set; } = new();
    [BindProperty] public SaleInput Sale { get; set; } = new();
    [BindProperty] public IFormFile? Upload { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public RentalProperty? Property { get; private set; }
    public PropertyBook Book { get; private set; } = PropertyBook.Empty;
    public IReadOnlyList<Attachment> Files { get; private set; } = [];
    public IReadOnlyList<Loan> Loans { get; private set; } = [];
    public IReadOnlyList<LoanEntry> LoanEntries { get; private set; } = [];
    public IReadOnlyDictionary<int, int> LoanLinks { get; private set; } = new Dictionary<int, int>();
    public RentalSale? SavedSale { get; private set; }
    public RentalSaleResult? SaleResult { get; private set; }
    public decimal CapitalJobs { get; private set; }

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    public int ThisYear => FinancialYear.EndYearFor(Today);

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } other)
            return other;
        return Page();
    }

    public async Task<IActionResult> OnPostDetailsAsync() => await RunAsync("details", p =>
        rentals.SaveDetailsAsync(p, Id!.Value, Details.PayoutCode, Details.State, Details.Value, Details.Valued), "Details saved.");

    public async Task<IActionResult> OnPostAddOwnerAsync() => await RunAsync("details", p =>
        rentals.AddOwnerAsync(p, Id!.Value, Line.Who, Line.Amount), "Owner added.");

    public async Task<IActionResult> OnPostDeleteOwnerAsync(int ownerId) => await RunAsync("details", p => rentals.DeleteOwnerAsync(p, ownerId), "Owner removed.");

    public async Task<IActionResult> OnPostTenancyAsync()
    {
        var t = TenancyForm;
        if (t.Start is null)
            return Back("tenancies", ProcResult.Failed("Enter the date the tenancy starts."), "");
        return await RunAsync("tenancies", p => book.SaveTenancyAsync(p, new Tenancy(t.Id, Id!.Value, t.Tenants?.Trim() ?? "", t.Email, t.Phone, t.Start.Value,
            t.LeaseEnd, t.Rent, t.Frequency, t.Bond, t.BondLodgement, t.RentReview, t.Vacated, t.Note)), t.Id == 0 ? "Tenancy added." : "Tenancy saved.");
    }

    public async Task<IActionResult> OnPostDeleteTenancyAsync(int itemId) =>
        await RunAsync("tenancies", p => book.DeleteTenancyAsync(p, itemId), "Tenancy deleted, with its rent payments.");

    public async Task<IActionResult> OnPostPaymentAsync()
    {
        if (Line.TenancyId is not { } tenancy || Line.Date is not { } date)
            return Back("tenancies", ProcResult.Failed("Enter the date the rent was paid."), "");
        return await RunAsync("tenancies", p => book.AddPaymentAsync(p, new RentPayment(0, tenancy, date, Line.Amount, Line.Description)), $"Rent of {Line.Amount:C} recorded.");
    }

    public async Task<IActionResult> OnPostDeletePaymentAsync(int itemId) => await RunAsync("tenancies", p => book.DeletePaymentAsync(p, itemId), "Payment deleted.");

    public async Task<IActionResult> OnPostBillAsync()
    {
        if (Line.Date is not { } date)
            return Back("bills", ProcResult.Failed("Enter the date of the bill."), "");
        var category = TenantBillCategories.Keys.Contains(Line.Kind ?? "") ? Line.Kind! : "Water";
        return await RunAsync("bills", p => book.SaveBillAsync(p, new TenantBill(Line.Id, Id!.Value, Line.TenancyId, category, Line.Description?.Trim() ?? "",
            date, Line.Amount, Line.Paid)), "Bill to the tenant saved.");
    }

    /// <summary>Marks a bill to the tenant as paid today (or on the date given).</summary>
    public async Task<IActionResult> OnPostBillPaidAsync(int itemId)
    {
        if (await LoadAsync() is { } other)
            return other;
        if (Book.Bills.FirstOrDefault(b => b.Id == itemId) is not { } bill)
            return Back("bills", ProcResult.Failed("That bill was not found."), "");
        return Back("bills", await book.SaveBillAsync(Portfolio!.PortfolioId, bill with { Paid = Line.Paid ?? Today }), "Bill marked as paid.");
    }

    public async Task<IActionResult> OnPostDeleteBillAsync(int itemId) => await RunAsync("bills", p => book.DeleteBillAsync(p, itemId), "Bill deleted.");

    public async Task<IActionResult> OnPostJobAsync()
    {
        if (Line.Date is not { } date)
            return Back("maintenance", ProcResult.Failed("Enter the date the work was done."), "");
        var treatment = JobTreatments.All.Contains(Line.Kind ?? "") ? Line.Kind! : JobTreatments.Repair;
        return await RunAsync("maintenance", p => book.SaveJobAsync(p, new RentalJob(Line.Id, Id!.Value, date, Line.Description?.Trim() ?? "", Line.Who, Line.Amount,
            treatment, Line.Reference)), "Job saved.");
    }

    public async Task<IActionResult> OnPostDeleteJobAsync(int itemId) => await RunAsync("maintenance", p => book.DeleteJobAsync(p, itemId), "Job deleted.");

    /// <summary>Adds a capital job (an improvement or initial repair) as capital works at 2.5% from the date it was done.</summary>
    public async Task<IActionResult> OnPostJobToAssetAsync(int itemId)
    {
        if (await LoadAsync() is { } other)
            return other;
        if (Book.Jobs.FirstOrDefault(j => j.Id == itemId) is not { } job)
            return Back("maintenance", ProcResult.Failed("That job was not found."), "");
        var r = await rentals.SaveAssetAsync(Portfolio!.PortfolioId, new RentalAsset(0, job.PropertyId, RentalAssetKinds.Building, job.Description, job.Done,
            job.Cost, null, null, 2.5m, null));
        return Back("maintenance", r, "Added as capital works at 2.5% a year. Change it on Yearly figures and assets if a depreciating asset part should be split out.");
    }

    /// <summary>Splits a capital job: the parts named become depreciating assets, the rest capital works at 2.5%.</summary>
    public async Task<IActionResult> OnPostJobSplitAsync(int itemId, string?[] partDesc, decimal?[] partAmount, string?[] partMethod, decimal?[] partLife)
    {
        if (await LoadAsync() is { } other)
            return other;
        if (Book.Jobs.FirstOrDefault(j => j.Id == itemId) is not { IsCapital: true } job)
            return Back("maintenance", ProcResult.Failed("Only an initial repair or improvement is split into assets."), "");
        var parts = partDesc.Select((d, i) => new JobPart(d ?? "", i < partAmount.Length ? partAmount[i] ?? 0 : 0,
            i < partMethod.Length ? partMethod[i] ?? RentalAssetKinds.DiminishingValue : RentalAssetKinds.DiminishingValue, i < partLife.Length ? partLife[i] : null));
        var (assets, error) = JobSplit.Build(job, parts);
        if (error is not null)
            return Back("maintenance", ProcResult.Failed(error), "");
        foreach (var a in assets)
        {
            var r = await rentals.SaveAssetAsync(Portfolio!.PortfolioId, a);
            if (!r.Ok)
                return Back("maintenance", r, "");
        }
        return Back("maintenance", ProcResult.Success(), $"Split into {assets.Count} asset{(assets.Count == 1 ? "" : "s")}. See them on Yearly figures and assets.");
    }

    public async Task<IActionResult> OnPostInspectionAsync()
    {
        if (Line.Date is not { } date)
            return Back("inspections", ProcResult.Failed("Enter the date of the inspection."), "");
        var kind = InspectionKinds.All.Contains(Line.Kind ?? "") ? Line.Kind! : InspectionKinds.Routine;
        return await RunAsync("inspections", p => book.SaveInspectionAsync(p, new RentalInspection(Line.Id, Id!.Value, Line.TenancyId, kind, date, Line.Description)),
            "Inspection saved. Add photos to it below.");
    }

    public async Task<IActionResult> OnPostDeleteInspectionAsync(int itemId) =>
        await RunAsync("inspections", p => book.DeleteInspectionAsync(p, itemId), "Inspection deleted.");

    public async Task<IActionResult> OnPostComplianceAsync() => await RunAsync("compliance", p =>
        book.SaveComplianceAsync(p, new ComplianceItem(Line.Id, Id!.Value, Line.Description?.Trim() ?? "", Line.Date, Line.Months, Line.Due, Line.Reference)),
        "Reminder saved.");

    /// <summary>Records a check or renewal as done today, so the next one is due a period from now.</summary>
    public async Task<IActionResult> OnPostComplianceDoneAsync(int itemId)
    {
        if (await LoadAsync() is { } other)
            return other;
        if (Book.Compliance.FirstOrDefault(c => c.Id == itemId) is not { } item)
            return Back("compliance", ProcResult.Failed("That reminder was not found."), "");
        return Back("compliance", await book.SaveComplianceAsync(Portfolio!.PortfolioId, item with { LastDone = Today, Due = null }), $"{item.Kind} recorded as done today.");
    }

    public async Task<IActionResult> OnPostDeleteComplianceAsync(int itemId) =>
        await RunAsync("compliance", p => book.DeleteComplianceAsync(p, itemId), "Reminder deleted.");

    public async Task<IActionResult> OnPostPresetsAsync()
    {
        if (await LoadAsync() is { } other)
            return other;
        var added = await book.AddPresetsAsync(Portfolio!.PortfolioId, Property!.Id, Property.State);
        return Back("compliance", ProcResult.Success(), added == 0 ? "The usual checks are already there." : $"Added {added} usual check{(added == 1 ? "" : "s")}. Enter when each was last done.");
    }

    /// <summary>A file on the property (kind Property), a maintenance job (RentalJob) or an inspection (Inspection).</summary>
    public async Task<IActionResult> OnPostUploadAsync(string kind, int? target)
    {
        if (await LoadAsync() is { } other)
            return other;
        var section = kind switch { AttachmentKinds.RentalJob => "maintenance", AttachmentKinds.Inspection => "inspections", _ => "documents" };
        if (kind is not (AttachmentKinds.Property or AttachmentKinds.RentalJob or AttachmentKinds.Inspection))
            return Back(section, ProcResult.Failed("Choose where the file goes."), "");
        if (Upload is null || AttachmentRules.Validate(Upload.FileName, Upload.Length) is { } invalid)
            return Back(section, ProcResult.Failed(Upload is null ? "Choose a file to upload." : AttachmentRules.Validate(Upload.FileName, Upload.Length)!), "");
        using var ms = new MemoryStream();
        await Upload.CopyToAsync(ms);
        var r = await attachments.AddAsync(Portfolio!.PortfolioId, CurrentPortfolio.UserId(User), kind, kind == AttachmentKinds.Property ? Property!.Id : target,
            kind == AttachmentKinds.Property ? null : ThisYear, Upload.FileName, ms.ToArray(), Line.Description);
        return Back(section, r, $"{AttachmentRules.CleanName(Upload.FileName)} uploaded.");
    }

    public async Task<IActionResult> OnPostDeleteFileAsync(int itemId) =>
        await RunAsync("documents", p => attachments.DeleteAsync(p, itemId), "File deleted.");

    public async Task<IActionResult> OnPostLinkLoanAsync(int loanId, bool link) =>
        await RunAsync("loans", p => rentals.LinkLoanAsync(p, loanId, link ? Id!.Value : 0), link ? "Loan linked: its interest is now this property's interest." : "Loan unlinked.");

    public async Task<IActionResult> OnPostSaleAsync()
    {
        var s = Sale;
        if (s.Acquired is null || s.Contract is null)
            return Back("sale", ProcResult.Failed("Enter the purchase date and the sale contract date."), "");
        return await RunAsync("sale", p => rentals.SaveSaleAsync(p, new RentalSale(Id!.Value, s.Acquired.Value, s.Contract.Value, s.Proceeds, s.PurchasePrice,
            s.AcquisitionCosts, s.HoldingCosts, s.Improvements, s.TitleCosts, s.DaysMainResidence, s.DaysAbsence, s.FirstRented,
            s.FirstRented is null ? null : s.MarketValueFirstRented, s.Note)), "Sale saved. The gain is in the capital gains report for the year of the contract.");
    }

    public async Task<IActionResult> OnPostDeleteSaleAsync() => await RunAsync("sale", p => rentals.DeleteSaleAsync(p, Id!.Value), "Sale removed.");

    private async Task<IActionResult> RunAsync(string section, Func<int, Task<ProcResult>> action, string message)
    {
        if (await LoadAsync() is { } other)
            return other;
        return Back(section, await action(Portfolio!.PortfolioId), message);
    }

    private IActionResult Back(string section, ProcResult r, string message)
    {
        TempData[r.Ok ? "Message" : "Error"] = r.Ok ? message : r.Error;
        return RedirectToPage(null, null, new { id = Id }, section);
    }

    /// <summary>Null when the page can be shown; otherwise where to go instead (the property list when none was picked).</summary>
    private async Task<IActionResult?> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return HttpMethods.IsGet(Request.Method) ? Page() : Forbid();
        if (Id is null)
            return RedirectToPage("/Rentals/Index");
        Property = await rentals.GetPropertyAsync(Portfolio.PortfolioId, Id.Value);
        if (Property is null)
            return NotFound();
        var id = Portfolio.PortfolioId;
        Book = (await book.GetAsync(id)).For(Property.Id);
        Files = (await attachments.ListAsync(id)).Where(a =>
            (a.Kind == AttachmentKinds.Property && a.TargetId == Property.Id)
            || (a.Kind == AttachmentKinds.RentalJob && Book.Jobs.Any(j => j.Id == a.TargetId))
            || (a.Kind == AttachmentKinds.Inspection && Book.Inspections.Any(i => i.Id == a.TargetId))).ToList();
        (Loans, LoanEntries) = await loans.GetAsync(id);
        LoanLinks = await rentals.GetLoanLinksAsync(id);
        CapitalJobs = Book.Jobs.Where(j => j.IsCapital).Sum(j => j.Cost);
        if ((await rentals.GetSaleResultsAsync(id)).FirstOrDefault(r => r.Property.Id == Property.Id) is { Result: { } result })
        {
            SavedSale = result.Sale;
            SaleResult = result;
        }
        // Fill the details form on a GET only: a POST carries what was typed
        if (HttpMethods.IsGet(Request.Method))
            Details = new DetailsInput { PayoutCode = Property.PayoutCode, State = Property.State, Value = Property.Value, Valued = Property.Valued };
        return null;
    }
}
