using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Debt;

public static class FixedIncomeKinds
{
    public const string TermDeposit = "TermDeposit", Bond = "Bond", Hybrid = "Hybrid";
    public static readonly string[] All = [TermDeposit, Bond, Hybrid];
    public static string Text(string k) => k switch { TermDeposit => "Term deposit", Bond => "Bond", _ => "Hybrid" };
}

public static class InterestFrequency
{
    public const string AtMaturity = "AtMaturity", Monthly = "Monthly", Quarterly = "Quarterly", HalfYearly = "HalfYearly", Yearly = "Yearly";
    public static readonly string[] All = [AtMaturity, Monthly, Quarterly, HalfYearly, Yearly];

    public static string Text(string f) => f switch
    {
        AtMaturity => "At maturity",
        Monthly => "Monthly",
        Quarterly => "Quarterly",
        HalfYearly => "Every six months",
        _ => "Yearly",
    };

    public static int? Months(string f) => f switch { Monthly => 1, Quarterly => 3, HalfYearly => 6, Yearly => 12, _ => null };
}

/// <summary>A term deposit, bond or hybrid (tblFixedIncome). Interest is simple interest on the face value, actual days over 365.</summary>
public sealed record FixedIncomeHolding(int Id, string Kind, string Name, string? ExchangeCode, string? StockCode, decimal FaceValue,
    decimal RatePercent, DateOnly Start, DateOnly? Maturity, string Frequency, string? Comment)
{
    public bool HeldOn(DateOnly d) => d >= Start && (Maturity is not { } m || d < m);
}

/// <summary>A holding's interest for a year: what it earned in the year (accruals) and what had built up unpaid at the year end.</summary>
public sealed record FixedIncomeYear(FixedIncomeHolding Holding, decimal Earned, decimal AccruedAtEnd, DateOnly? LastPayment, DateOnly? NextPayment);

public static class FixedIncomeCalc
{
    /// <summary>Interest for the days from (exclusive) to (inclusive), within the holding's life.</summary>
    public static decimal Interest(FixedIncomeHolding h, DateOnly from, DateOnly to)
    {
        var start = from < h.Start ? h.Start : from;
        var end = h.Maturity is { } m && to > m ? m : to;
        var days = end.DayNumber - start.DayNumber;
        return days <= 0 ? 0 : Math.Round(h.FaceValue * h.RatePercent / 100m * days / 365m, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The interest payment date on or before the date (the start when none yet). Payments run back from maturity when
    /// there is one, else forward from the start.
    /// </summary>
    public static DateOnly LastPaymentOnOrBefore(FixedIncomeHolding h, DateOnly date)
    {
        if (InterestFrequency.Months(h.Frequency) is not { } months)
            return h.Maturity is { } m && date >= m ? m : h.Start;
        var last = h.Start;
        foreach (var d in PaymentDates(h, months))
        {
            if (d > date) break;
            last = d;
        }
        return last;
    }

    public static DateOnly? NextPaymentAfter(FixedIncomeHolding h, DateOnly date)
    {
        if (InterestFrequency.Months(h.Frequency) is not { } months)
            return h.Maturity is { } m && m > date ? m : null;
        foreach (var d in PaymentDates(h, months))
            if (d > date)
                return d;
        return null;
    }

    private static IEnumerable<DateOnly> PaymentDates(FixedIncomeHolding h, int months)
    {
        if (h.Maturity is { } m)
        {
            var dates = new List<DateOnly>();
            for (var d = m; d > h.Start; d = d.AddMonths(-months))
                dates.Add(d);
            dates.Reverse();
            foreach (var d in dates)
                yield return d;
            yield break;
        }
        // No maturity (a perpetual hybrid): from the start, for up to 100 years
        for (var (d, i) = (h.Start.AddMonths(months), 0); i < 1200; d = d.AddMonths(months), i++)
            yield return d;
    }

    public static FixedIncomeYear Year(FixedIncomeHolding h, DateOnly from, DateOnly to)
    {
        var earned = Interest(h, from.AddDays(-1), to);
        var last = LastPaymentOnOrBefore(h, to);
        var accrued = h.HeldOn(to) ? Interest(h, last, to) : 0;
        return new FixedIncomeYear(h, earned, accrued, last == h.Start ? null : last, NextPaymentAfter(h, to));
    }

    public static IReadOnlyList<string> Validate(FixedIncomeHolding h)
    {
        var errors = new List<string>();
        if (!FixedIncomeKinds.All.Contains(h.Kind)) errors.Add("Choose the type of investment.");
        if (string.IsNullOrWhiteSpace(h.Name)) errors.Add("Enter a name, e.g. the bank and term.");
        if (h.FaceValue <= 0) errors.Add("Enter the amount invested (the face value).");
        if (h.RatePercent is < 0 or > 100) errors.Add("Check the interest rate.");
        if (!InterestFrequency.All.Contains(h.Frequency)) errors.Add("Choose how often interest is paid.");
        if (h.Maturity is { } m && m <= h.Start) errors.Add("The maturity date must be after the start date.");
        if (h.Frequency == InterestFrequency.AtMaturity && h.Maturity is null) errors.Add("Interest paid at maturity needs a maturity date.");
        return errors;
    }
}

/// <summary>tblFixedIncome, written through the procs in 2026-10-21-12-debt-fixed-income.sql.</summary>
public sealed class FixedIncomeService(IDbConnectionFactory db)
{
    private sealed record Row(int Id, string Kind, string Name, string? ExchangeCode, string? StockCode, double FaceValue, double RatePercent,
        DateTime Start, DateTime? Maturity, string Frequency, string? Comment);

    /// <summary>Empty until the migration has been run.</summary>
    public async Task<IReadOnlyList<FixedIncomeHolding>> GetAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync<Row>("""
                SELECT iFixedIncomeID AS Id, strKind AS Kind, strName AS Name, strStockExchangeCode AS ExchangeCode, strStockCode AS StockCode,
                       dblFaceValue AS FaceValue, dblRatePercent AS RatePercent, CAST(dtmStart AS datetime) AS Start,
                       CAST(dtmMaturity AS datetime) AS Maturity, strFrequency AS Frequency, strComment AS Comment
                FROM tblFixedIncome WHERE iUserID = @portfolioId ORDER BY dtmMaturity, strName
                """, new { portfolioId });
            return rows.Select(r => new FixedIncomeHolding(r.Id, r.Kind, r.Name, r.ExchangeCode, r.StockCode, (decimal)r.FaceValue,
                (decimal)r.RatePercent, DateOnly.FromDateTime(r.Start), r.Maturity is { } m ? DateOnly.FromDateTime(m) : null, r.Frequency, r.Comment)).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    public async Task<ProcResult> SaveAsync(int portfolioId, FixedIncomeHolding h)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveFixedIncome", new
        {
            iUserID = portfolioId, iFixedIncomeID = h.Id, strKind = h.Kind, strName = h.Name.Trim(), strStockExchangeCode = h.ExchangeCode,
            strStockCode = h.StockCode, dblFaceValue = (double)h.FaceValue, dblRatePercent = (double)h.RatePercent,
            dtmStart = h.Start.ToDateTime(TimeOnly.MinValue), dtmMaturity = h.Maturity?.ToDateTime(TimeOnly.MinValue),
            strFrequency = h.Frequency, strComment = string.IsNullOrWhiteSpace(h.Comment) ? null : h.Comment.Trim(),
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> DeleteAsync(int portfolioId, int id)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteFixedIncome", new { iUserID = portfolioId, iFixedIncomeID = id },
            commandType: CommandType.StoredProcedure), "Success");
    }
}
