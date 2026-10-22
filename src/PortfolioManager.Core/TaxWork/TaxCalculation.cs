using PortfolioManager.Core.Clients;

namespace PortfolioManager.Core.TaxWork;

/// <summary>
/// Tax on a company's or complying SMSF's taxable income, and what's left to pay after offsets and
/// credits. A company's franking offset can't make a refund: the excess is shown, and converts to a
/// tax loss of ExcessFrankingLoss. A fund's franking offset is refundable. Payable is negative for a refund.
/// </summary>
public sealed record TaxCalculation(
    decimal RatePercent,
    decimal TaxableIncome,
    decimal GrossTax,
    decimal ForeignOffset,
    decimal FrankingOffset,
    decimal ExcessFranking,
    decimal TfnCredits,
    decimal PaygInstalments,
    decimal SupervisoryLevy)
{
    /// <summary>The ATO's annual levy for an SMSF (2024-25 and 2025-26 rate).</summary>
    public const decimal SmsfLevy = 259m;

    public const decimal DefaultCompanyRate = 30m;
    public const decimal BaseRateEntityRate = 25m;
    public const decimal SmsfRate = 15m;

    /// <summary>Tax after the offsets: the current year income tax expense.</summary>
    public decimal TaxAfterOffsets => GrossTax - ForeignOffset - FrankingOffset;

    public decimal Payable => TaxAfterOffsets - TfnCredits - PaygInstalments + SupervisoryLevy;

    public decimal ExcessFrankingLoss => RatePercent > 0 ? TaxReconciliation.Round(ExcessFranking / (RatePercent / 100m)) : 0;

    /// <summary>The rate for the entity type, or null for one that isn't taxed at a flat rate.</summary>
    public static decimal? RateFor(string entityType, decimal? companyRate) => entityType switch
    {
        ClientEntityTypes.Company => companyRate ?? DefaultCompanyRate,
        ClientEntityTypes.Smsf => SmsfRate,
        _ => null,
    };

    public static TaxCalculation? For(string entityType, TaxReconciliation rec, TaxIncomeFacts income, TaxYearSettings settings)
    {
        if (RateFor(entityType, settings.TaxRatePercent) is not { } rate)
            return null;
        var smsf = entityType == ClientEntityTypes.Smsf;

        // Tax is worked out on taxable income in whole dollars
        var taxable = Math.Max(Math.Floor(rec.TaxableIncome), 0);
        var gross = TaxReconciliation.Round(taxable * rate / 100m);
        var foreign = Math.Min(income.ForeignTax, gross);
        var left = gross - foreign;
        var franking = smsf ? income.FrankingCredits : Math.Min(income.FrankingCredits, left);
        return new TaxCalculation(rate, taxable, gross, foreign, franking, income.FrankingCredits - franking,
            income.TfnWithheld, settings.PaygInstalments, smsf ? SmsfLevy : 0);
    }
}
