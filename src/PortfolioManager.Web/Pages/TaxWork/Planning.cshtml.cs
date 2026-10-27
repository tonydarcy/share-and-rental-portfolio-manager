using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>
/// Tax planning: for an individual, the year's investment figures plus what the user enters (salary, PAYG), then the same with a
/// what-if sale, a deductible super contribution and extra deductions, side by side. For a company or SMSF, the year's taxable
/// income with extra income, deductions, a sale and (for a fund) contributions. For a trust, a split of its net income between
/// beneficiaries and what it costs each of them. Nothing is saved.
/// </summary>
public class PlanningModel(CurrentPortfolio current, TaxWorkpaperService workpapers, LockedPeriodService locks, TrustBeneficiaryService beneficiaries)
    : TaxYearPageModel(current, workpapers, locks)
{
    public sealed class BeneficiaryRow
    {
        public string? Name { get; set; }
        public string Type { get; set; } = BeneficiaryTypes.Individual;
        public decimal Percent { get; set; }
        public decimal OtherIncome { get; set; }
    }

    [BindProperty(SupportsGet = true)] public bool Senior { get; set; }
    [BindProperty(SupportsGet = true)] public decimal ExtraIncome { get; set; }
    [BindProperty(SupportsGet = true)] public decimal ExtraContributions { get; set; }
    [BindProperty(SupportsGet = true)] public List<BeneficiaryRow> B { get; set; } = [];
    [BindProperty(SupportsGet = true)] public decimal? BucketRate { get; set; }

    public EntityPlanResult? EntityNow { get; private set; }
    public EntityPlanResult? EntityPlan { get; private set; }
    public TrustPlan? Trust { get; private set; }
    public bool IsEntity => Workpaper?.EntityType is ClientEntityTypes.Company or ClientEntityTypes.Smsf;
    public bool IsTrust => Workpaper?.EntityType == ClientEntityTypes.Trust;
    public MedicareLowIncome MedicareThresholds { get; private set; } = MedicareLowIncome.For(2026);
    public const int BeneficiaryRows = 6;
    [BindProperty(SupportsGet = true)] public decimal OtherIncome { get; set; }
    [BindProperty(SupportsGet = true)] public decimal Payg { get; set; }
    [BindProperty(SupportsGet = true)] public decimal OtherDeductions { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? SaleProceeds { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? SaleCost { get; set; }
    [BindProperty(SupportsGet = true)] public bool SaleLong { get; set; } = true;
    [BindProperty(SupportsGet = true)] public decimal Contribution { get; set; }
    [BindProperty(SupportsGet = true)] public decimal ExtraDeductions { get; set; }
    [BindProperty(SupportsGet = true)] public decimal FringeBenefits { get; set; }
    [BindProperty(SupportsGet = true)] public decimal ReportableSuper { get; set; }

    public PlanResult? Now { get; private set; }
    public PlanResult? Plan { get; private set; }
    public IndividualRates Rates { get; private set; } = IndividualRates.For(2026);
    public ContributionCaps Caps { get; private set; } = ContributionCaps.For(2026);
    public bool IsIndividual => Workpaper?.EntityType == ClientEntityTypes.Individual;

    public async Task<IActionResult> OnGetAsync()
    {
        // Planning is usually for the year under way
        Fy ??= FinancialYear.EndYearFor(Today);
        if (await LoadAsync() is { } refusal)
            return refusal;
        var w = Workpaper!;
        if (IsEntity)
        {
            WorkEntity(w);
            return Page();
        }
        if (IsTrust)
        {
            await WorkTrustAsync(w);
            return Page();
        }
        if (!IsIndividual)
            return Page();
        MedicareThresholds = MedicareLowIncome.For(Year);
        Rates = IndividualRates.For(Year);
        Caps = ContributionCaps.For(Year);
        var investment = w.Reconciliation.TaxableIncome - w.Cgt.NetCapitalGain;
        // Net investment losses for the ESS income test: a net loss on the rentals, or on the other investments
        var rental = w.Rental?.Net ?? 0;
        var now = new PlanInputs(OtherIncome, investment, rental, w.Cgt.NetCapitalGain, OtherDeductions, 0,
            w.Income.FrankingCredits, w.Income.TfnWithheld + w.Ess.TfnWithheld + Payg, w.Income.ForeignTax,
            w.Ess.UpfrontEligible, w.Ess.UpfrontNotEligible + w.Ess.Deferral + w.Ess.Pre2009, FringeBenefits, ReportableSuper,
            Math.Max(0, -rental) + Math.Max(0, -investment), Senior);
        Now = TaxPlanner.Work(now, Rates);
        var sale = SaleProceeds is { } p && SaleCost is { } c ? new WhatIfSale(p, c, SaleLong) : null;
        Plan = TaxPlanner.Work(now with
        {
            NetCapitalGain = TaxPlanner.NetGainWith(w.Cgt, sale),
            Deductions = OtherDeductions + ExtraDeductions,
            ConcessionalDeduction = Math.Max(0, Contribution),
        }, Rates);
        return Page();
    }

    private void WorkEntity(TaxWorkpaper w)
    {
        var smsf = w.EntityType == ClientEntityTypes.Smsf;
        var rate = TaxCalculation.RateFor(w.EntityType, w.Data.Settings.TaxRatePercent) ?? TaxCalculation.DefaultCompanyRate;
        var ecpi = smsf ? w.Data.Settings.EcpiPercent ?? 0 : 0;
        var sale = SaleProceeds is { } p && SaleCost is { } c ? new WhatIfSale(p, c, SaleLong) : null;
        var taxable = w.Reconciliation.TaxableIncome;
        EntityNow = EntityPlanner.Work(smsf, rate, taxable, w.Income, w.Data.Settings.PaygInstalments, ecpi, new EntityPlanChanges());
        EntityPlan = EntityPlanner.Work(smsf, rate, taxable, w.Income, w.Data.Settings.PaygInstalments, ecpi, new EntityPlanChanges(
            ExtraIncome, ExtraDeductions, TaxPlanner.NetGainWith(w.Cgt, sale) - w.Cgt.NetCapitalGain, Math.Max(0, ExtraContributions)));
    }

    private async Task WorkTrustAsync(TaxWorkpaper w)
    {
        if (B.Count == 0)
            B = (await beneficiaries.GetAsync(Portfolio!.PortfolioId, Year))
                .Select(b => new BeneficiaryRow { Name = b.Name, Type = b.Type == BeneficiaryTypes.Company ? BeneficiaryTypes.Company : BeneficiaryTypes.Individual, Percent = b.Percent })
                .ToList();
        while (B.Count < BeneficiaryRows)
            B.Add(new BeneficiaryRow());
        var rows = B.Take(BeneficiaryRows).Where(b => b.Percent > 0)
            .Select((b, i) => new PlanBeneficiary(b.Name is { Length: > 0 } n ? n.Trim() : $"Beneficiary {i + 1}",
                b.Type == BeneficiaryTypes.Company ? BeneficiaryTypes.Company : BeneficiaryTypes.Individual, Math.Clamp(b.Percent, 0, 100), b.OtherIncome))
            .ToList();
        Trust = TrustPlanner.Work(Year, Math.Max(0, w.Reconciliation.TaxableIncome), Math.Max(0, w.Cgt.NetCapitalGain), w.Income.FrankingCredits,
            w.Cgt.Rate.Applies, rows, BucketRate is 25m or 30m ? BucketRate.Value : TaxCalculation.BaseRateEntityRate);
    }
}
