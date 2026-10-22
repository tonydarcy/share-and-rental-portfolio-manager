using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>Trust distribution statement: the year's net income split between beneficiaries, and the trustee's resolution.</summary>
public class DistributionModel(CurrentPortfolio current, TaxWorkpaperService workpapers, LockedPeriodService locks,
    TrustBeneficiaryService beneficiaries, TaxYearService years) : TaxYearPageModel(current, workpapers, locks)
{
    [BindProperty]
    public TrustBeneficiaryInput Input { get; set; } = new();

    [BindProperty]
    public DateTime? ResolutionDate { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? Edit { get; set; }

    public IReadOnlyList<TrustBeneficiary> Beneficiaries { get; private set; } = [];
    public TrustDistributionStatement? Statement { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadPageAsync() is { } refusal)
            return refusal;
        if (Edit is { } id && Beneficiaries.FirstOrDefault(b => b.BeneficiaryId == id) is { } b)
            Input = new TrustBeneficiaryInput { BeneficiaryId = b.BeneficiaryId, Name = b.Name, Type = b.Type, Percent = b.Percent };
        ResolutionDate = Workpaper!.Data.Settings.ResolutionDate?.ToDateTime(TimeOnly.MinValue);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (await LoadPageAsync() is { } refusal)
            return refusal;
        return Done(await beneficiaries.SaveAsync(Portfolio!.PortfolioId, Year, YearEnd, Input), Input.BeneficiaryId is null ? "Beneficiary added." : "Beneficiary saved.");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int beneficiaryId)
    {
        if (await LoadPageAsync() is { } refusal)
            return refusal;
        return Done(await beneficiaries.DeleteAsync(Portfolio!.PortfolioId, Year, YearEnd, beneficiaryId), "Beneficiary removed.");
    }

    public async Task<IActionResult> OnPostCopyAsync()
    {
        if (await LoadPageAsync() is { } refusal)
            return refusal;
        return Done(await beneficiaries.CopyLastYearAsync(Portfolio!.PortfolioId, Year, YearEnd), "Last year's beneficiaries copied.");
    }

    public async Task<IActionResult> OnPostResolutionAsync()
    {
        if (await LoadPageAsync() is { } refusal)
            return refusal;
        var input = TaxYearInput.From(Workpaper!.Data.Settings);
        input.ResolutionDate = ResolutionDate;
        return Done(await years.SaveAsync(Portfolio!.PortfolioId, Year, YearEnd, input), "Resolution date saved.");
    }

    private async Task<IActionResult?> LoadPageAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        Beneficiaries = await beneficiaries.GetAsync(Portfolio!.PortfolioId, Year);
        Statement = TrustDistributionStatement.Build(TrustDistributionStatement.Components(Workpaper!), Beneficiaries);
        return null;
    }
}
