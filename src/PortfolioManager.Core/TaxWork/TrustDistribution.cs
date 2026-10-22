using System.Data;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.TaxWork;

public static class BeneficiaryTypes
{
    public const string Individual = "Individual";
    public const string Company = "Company";
    public const string Trust = "Trust";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All = [Individual, Company, Trust, Other];
}

public sealed record TrustBeneficiary(int BeneficiaryId, string Name, string Type, decimal Percent);

public sealed class TrustBeneficiaryInput
{
    public int? BeneficiaryId { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = BeneficiaryTypes.Individual;
    public decimal Percent { get; set; }
}

/// <summary>
/// The character of the trust's net income. DiscountGains and OtherGains are after capital losses
/// and before the discount: a beneficiary grosses its share up to these, applies its own losses and
/// discount, and reports them at item 18. Other is what's left once franked distributions, their
/// franking credits and the net capital gain are taken out.
/// </summary>
public sealed record DistributionComponents(
    decimal NetIncome,
    decimal Franked,
    decimal FrankingCredits,
    decimal DiscountGains,
    decimal OtherGains,
    decimal CgtDiscount,
    decimal Other,
    decimal ForeignTaxCredits,
    decimal TfnCredits)
{
    public static readonly DistributionComponents Zero = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    public decimal NetCapitalGain => DiscountGains + OtherGains - CgtDiscount;
    public decimal GrossCapitalGains => DiscountGains + OtherGains;

    public DistributionComponents Share(decimal percent)
    {
        decimal P(decimal x) => TaxReconciliation.Round(x * percent / 100m);
        return new(P(NetIncome), P(Franked), P(FrankingCredits), P(DiscountGains), P(OtherGains), P(CgtDiscount), P(Other), P(ForeignTaxCredits), P(TfnCredits));
    }

    public DistributionComponents Minus(DistributionComponents o) => new(
        NetIncome - o.NetIncome, Franked - o.Franked, FrankingCredits - o.FrankingCredits, DiscountGains - o.DiscountGains,
        OtherGains - o.OtherGains, CgtDiscount - o.CgtDiscount, Other - o.Other, ForeignTaxCredits - o.ForeignTaxCredits, TfnCredits - o.TfnCredits);

    public DistributionComponents Plus(DistributionComponents o) => Minus(Zero.Minus(o));
}

public sealed record BeneficiaryShare(TrustBeneficiary Beneficiary, DistributionComponents Amounts);

/// <summary>
/// The trust's net income split between the beneficiaries in proportion to their shares, with the
/// same mix of franked, capital and other income in each (no streaming). Rounding cents go to the
/// largest share so the columns add up. Whatever no beneficiary is entitled to is taxed to the trustee.
/// </summary>
public sealed record TrustDistributionStatement(
    DistributionComponents Total,
    IReadOnlyList<BeneficiaryShare> Shares,
    decimal TotalPercent,
    DistributionComponents Undistributed,
    IReadOnlyList<string> Warnings)
{
    public static DistributionComponents Components(TaxWorkpaper w)
    {
        var net = w.Reconciliation.TaxableIncome;
        if (net <= 0)
            return DistributionComponents.Zero;
        var cgt = w.Cgt;
        var discountGains = cgt.DiscountGains - cgt.LossesAppliedToDiscountGains;
        var otherGains = cgt.OtherGains - cgt.LossesAppliedToOtherGains;
        var franked = w.Income.Franked + w.Income.TrustFranked;
        var credits = w.Income.FrankingCredits;
        var other = net - cgt.NetCapitalGain - franked - credits;
        return new(net, franked, credits, discountGains, otherGains, cgt.Discount, other, w.Income.ForeignTax, w.Income.TfnWithheld);
    }

    public static TrustDistributionStatement Build(DistributionComponents total, IReadOnlyList<TrustBeneficiary> beneficiaries)
    {
        var warnings = new List<string>();
        var percent = beneficiaries.Sum(b => b.Percent);
        var shares = beneficiaries.Select(b => new BeneficiaryShare(b, total.Share(b.Percent))).ToList();

        if (percent == 100 && shares.Count > 0)
        {
            // Put the rounding on the largest share
            var sum = shares.Aggregate(DistributionComponents.Zero, (acc, s) => acc.Plus(s.Amounts));
            var largest = shares.IndexOf(shares.MaxBy(s => s.Beneficiary.Percent)!);
            shares[largest] = shares[largest] with { Amounts = shares[largest].Amounts.Plus(total.Minus(sum)) };
        }
        var distributed = shares.Aggregate(DistributionComponents.Zero, (acc, s) => acc.Plus(s.Amounts));
        var undistributed = total.Minus(distributed);

        if (total.NetIncome <= 0)
            warnings.Add("The trust has no net income for the year, so there is nothing to distribute. A loss stays in the trust and is carried forward.");
        else if (shares.Count == 0)
            warnings.Add("No beneficiaries have been entered for this year, so the trustee would be assessed on all of the net income.");
        else if (percent < 100)
            warnings.Add($"The shares add up to {percent:0.##}%. The trustee is assessed on the {100 - percent:0.##}% no beneficiary is presently entitled to (s99A rates).");
        if (total.Other < 0)
            warnings.Add("Expenses are more than the trust's other income, so part of the franked distributions or capital gains is absorbed. Check how the deductions are allocated before relying on the split.");
        return new(total, shares, percent, undistributed, warnings);
    }

    /// <summary>The trustee's resolution, paragraph by paragraph, for the trust deed to be checked against.</summary>
    public static IReadOnlyList<string> ResolutionWording(ClientDetails client, int year, IReadOnlyList<TrustBeneficiary> beneficiaries)
    {
        var yearEnd = $"30 June {year}";
        var list = string.Join("; ", beneficiaries.Select(b => $"{b.Name}: {b.Percent:0.####}%"));
        return
        [
            $"The trustee has considered the trust deed and the income of the trust for the year ending {yearEnd}.",
            $"RESOLVED that the income of the trust estate for the year ending {yearEnd}, as defined in the trust deed, be distributed to the following beneficiaries, who are presently entitled to it in these proportions: {list}.",
            "RESOLVED that any net capital gain and any franked distributions (with their franking credits) included in the trust's net income be distributed in the same proportions, and are not streamed to particular beneficiaries.",
            "RESOLVED that each beneficiary's entitlement be paid to them, or credited to their loan account in the trust and held for them, as the trustee decides.",
            $"This resolution is made on or before {yearEnd}.",
        ];
    }

    /// <summary>Who signs: the trustee's directors for a corporate trustee, otherwise the individual trustees.</summary>
    public static IReadOnlyList<string> Signatories(ClientDetails client)
    {
        var roles = client.CorporateTrustee ? new[] { OfficeholderRoles.TrusteeDirector, OfficeholderRoles.Director } : [OfficeholderRoles.Trustee];
        var names = client.Officeholders.Where(o => roles.Contains(o.Role)).Select(o => o.Name).ToList();
        if (names.Count == 0 && !string.IsNullOrWhiteSpace(client.TrusteeName))
            names.Add(client.TrusteeName!);
        return names;
    }
}

/// <summary>A year's beneficiaries (tblTrustBeneficiary).</summary>
public sealed class TrustBeneficiaryService(IDbConnectionFactory db)
{
    public async Task<IReadOnlyList<TrustBeneficiary>> GetAsync(int portfolioId, int year)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync("uspGetTrustBeneficiaries", new { iUserId = portfolioId, iYear = year }, commandType: CommandType.StoredProcedure);
        return Map(rows.Cast<IDictionary<string, object?>>());
    }

    public static IReadOnlyList<TrustBeneficiary> Map(IEnumerable<IDictionary<string, object?>> rows) =>
        rows.Select(r => new TrustBeneficiary(
                Db.Int(r, "iBeneficiaryID") ?? 0,
                Db.Str(r, "strName"),
                Db.Str(r, "strType") is { Length: > 0 } t ? t : BeneficiaryTypes.Individual,
                Db.Dec(r, "dblPercent") ?? 0))
            .Where(b => b.BeneficiaryId > 0)
            .ToList();

    public async Task<ProcResult> SaveAsync(int portfolioId, int year, DateOnly yearEnd, TrustBeneficiaryInput input)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync<(string? Result, int? BeneficiaryId)>("uspSaveTrustBeneficiary", new
        {
            iUserId = portfolioId,
            iYear = year,
            dtYearEnd = yearEnd.ToDateTime(TimeOnly.MinValue),
            iBeneficiaryID = input.BeneficiaryId,
            strName = input.Name?.Trim() ?? "",
            strType = BeneficiaryTypes.All.Contains(input.Type) ? input.Type : BeneficiaryTypes.Other,
            dblPercent = input.Percent,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.BeneficiaryId);
    }

    public async Task<ProcResult> DeleteAsync(int portfolioId, int year, DateOnly yearEnd, int beneficiaryId)
    {
        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspDeleteTrustBeneficiary", new
        {
            iUserId = portfolioId, iYear = year, dtYearEnd = yearEnd.ToDateTime(TimeOnly.MinValue), iBeneficiaryID = beneficiaryId,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    public async Task<ProcResult> CopyLastYearAsync(int portfolioId, int year, DateOnly yearEnd)
    {
        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspCopyTrustBeneficiaries", new
        {
            iUserId = portfolioId, iYear = year, dtYearEnd = yearEnd.ToDateTime(TimeOnly.MinValue),
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }
}
