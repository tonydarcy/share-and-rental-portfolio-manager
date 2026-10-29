namespace PortfolioManager.Core.Payouts;

/// <summary>The rules of the Edit payout page, shared with the API's edit_payout.</summary>
public static class PayoutEdit
{
    public static PayoutInput FromRecord(PayoutRecord r) => new()
    {
        StockCode = r.StockCode ?? "",
        StockExchangeCode = r.StockExchangeCode ?? "",
        Date = r.Date ?? DateTime.Today,
        Total = Money(r.Total),
        Net = r.Net is null ? null : Money(r.Net),
        Franked = Money(r.Franked),
        Unfranked = Money(r.Unfranked),
        Interest = Money(r.Interest),
        TaxWithheld = Money(r.TaxWithheld),
        ForeignTax = Money(r.ForeignTax),
        Other = Money(r.Other),
        FrankingCredit = Money(r.FrankingCredit),
        TrustIncome = Money(r.TrustIncome),
        Comment = r.Comment,
        Confirmed = string.Equals(r.Confirmed?.Trim(), "Yes", StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>
    /// Readies the changed payout for <see cref="PayoutService.UpdateAsync"/>: a DRP payout keeps its code and amounts
    /// (they belong with its reinvestment trade), and an old payout whose figures don't add up can still have its code,
    /// date or comment changed as long as its amounts stay the same. Returns whether the stored amounts are kept, and any errors.
    /// </summary>
    public static (bool KeepAmounts, IReadOnlyList<string> Errors) Prepare(PayoutRecord record, PayoutInput input)
    {
        var amountsLocked = record.ReinvestmentTradeId is not null;
        var stored = FromRecord(record);
        if (amountsLocked)
        {
            input.StockCode = stored.StockCode;
            input.StockExchangeCode = stored.StockExchangeCode;
        }

        var keepAmounts = amountsLocked || SameAmounts(stored, input);
        if (keepAmounts)
            CopyAmounts(stored, input);

        var errors = input.ValidateDetails().ToList();
        if (!amountsLocked)
            errors.AddRange(input.ValidateCode());
        if (!keepAmounts)
            errors.AddRange(input.ValidateAmounts());
        return (keepAmounts, errors);
    }

    private static decimal Money(double? v) => Math.Round((decimal)(v ?? 0), 2);

    private static void CopyAmounts(PayoutInput from, PayoutInput to)
    {
        to.Total = from.Total;
        to.Net = from.Net;
        to.Franked = from.Franked;
        to.Unfranked = from.Unfranked;
        to.Interest = from.Interest;
        to.TaxWithheld = from.TaxWithheld;
        to.ForeignTax = from.ForeignTax;
        to.Other = from.Other;
        to.FrankingCredit = from.FrankingCredit;
        to.TrustIncome = from.TrustIncome;
    }

    private static bool SameAmounts(PayoutInput a, PayoutInput b) =>
        a.Total == b.Total && a.Franked == b.Franked && a.Unfranked == b.Unfranked && a.Interest == b.Interest
        && a.TaxWithheld == b.TaxWithheld && a.ForeignTax == b.ForeignTax && a.Other == b.Other
        && a.FrankingCredit == b.FrankingCredit && a.TrustIncome == b.TrustIncome;
}
