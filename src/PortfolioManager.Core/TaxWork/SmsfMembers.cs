using System.Data;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.TaxWork;

public static class SmsfPhases
{
    public const string Accumulation = "Accumulation";
    public const string Retirement = "Retirement";
    public static readonly IReadOnlyList<string> All = [Accumulation, Retirement];

    public static string Label(string phase) => phase == Retirement ? "Retirement phase (pension)" : "Accumulation";
}

public sealed record SmsfMember(int MemberId, string Name, DateOnly? BirthDate)
{
    /// <summary>Age on the first day of the financial year, which sets the pension factor and NCC eligibility.</summary>
    public int? AgeAt(DateOnly date)
    {
        if (BirthDate is not { } b) return null;
        var age = date.Year - b.Year;
        return date < b.AddYears(age) ? age - 1 : age;
    }
}

/// <summary>A member's account in one phase for a year: what was entered, before earnings and tax.</summary>
public sealed record SmsfMemberAccount(
    int MemberAccountId,
    int MemberId,
    int Year,
    string Phase,
    decimal Opening,
    decimal Concessional,
    decimal NonConcessional,
    decimal RolloverIn,
    decimal PensionPaid,
    decimal LumpSum,
    decimal Insurance,
    DateOnly? PensionStart = null,
    decimal? StartBalance = null)
{
    public bool IsPension => Phase == SmsfPhases.Retirement;

    /// <summary>The pension started during the year (on a day after 1 July), so its first minimum is pro-rated.</summary>
    public bool StartedInYear(DateOnly yearStart) => IsPension && PensionStart is { } s && s > yearStart;
    public decimal Inflows => Concessional + NonConcessional + RolloverIn;
    public decimal Outflows => PensionPaid + LumpSum + Insurance;

    /// <summary>The balance earnings are shared on: opening plus half the year's net flows (they're assumed to happen evenly).</summary>
    public decimal Weighted => Math.Max(0, Opening + (Inflows - Outflows) / 2m);
}

public sealed class SmsfMemberInput
{
    public int? MemberId { get; set; }
    public string Name { get; set; } = "";
    public DateTime? BirthDate { get; set; }
}

public sealed class SmsfAccountInput
{
    public int? MemberAccountId { get; set; }
    public int MemberId { get; set; }
    public string Phase { get; set; } = SmsfPhases.Accumulation;
    public decimal Opening { get; set; }
    public decimal Concessional { get; set; }
    public decimal NonConcessional { get; set; }
    public decimal RolloverIn { get; set; }
    public decimal PensionPaid { get; set; }
    public decimal LumpSum { get; set; }
    public decimal Insurance { get; set; }

    /// <summary>A pension that started during the year: the day it started and the balance then. Blank: it ran all year.</summary>
    public DateTime? PensionStart { get; set; }
    public decimal? StartBalance { get; set; }
}

public sealed record SmsfYearData(IReadOnlyList<SmsfMember> Members, IReadOnlyList<SmsfMemberAccount> Accounts)
{
    public static readonly SmsfYearData Empty = new([], []);

    public SmsfTaxInputs TaxInputs => new(Accounts.Sum(a => a.Concessional), Accounts.Sum(a => a.NonConcessional), Accounts.Sum(a => a.RolloverIn));
}

/// <summary>
/// The contribution caps and transfer balance cap for a year. Known is false when the year's caps
/// aren't loaded and the latest known ones were used instead.
/// </summary>
public sealed record ContributionCaps(int Year, decimal Concessional, decimal NonConcessional, decimal TransferBalanceCap, bool Known)
{
    private static readonly SortedDictionary<int, (decimal Cc, decimal Ncc, decimal Tbc)> ByYear = new()
    {
        [2022] = (27_500, 110_000, 1_700_000),
        [2023] = (27_500, 110_000, 1_700_000),
        [2024] = (27_500, 110_000, 1_900_000),
        [2025] = (30_000, 120_000, 1_900_000),
        [2026] = (30_000, 120_000, 2_000_000),
    };

    public static ContributionCaps For(int year)
    {
        if (ByYear.TryGetValue(year, out var c))
            return new(year, c.Cc, c.Ncc, c.Tbc, true);
        var nearest = year > ByYear.Keys.Last() ? ByYear.Keys.Last() : ByYear.Keys.First();
        var n = ByYear[nearest];
        return new(year, n.Cc, n.Ncc, n.Tbc, false);
    }

    /// <summary>
    /// The most non-concessional contributions a member can make this year, from their total super
    /// balance at the previous 30 June: three years' cap brought forward, two, one, or none. Members
    /// 75 or older at the start of the year can't use the bring-forward (or, mostly, contribute at all).
    /// </summary>
    public decimal NonConcessionalLimit(decimal totalSuperBalance, int? age)
    {
        if (totalSuperBalance >= TransferBalanceCap || age >= 75) return 0;
        if (totalSuperBalance >= TransferBalanceCap - NonConcessional) return NonConcessional;
        if (totalSuperBalance >= TransferBalanceCap - 2 * NonConcessional) return 2 * NonConcessional;
        return 3 * NonConcessional;
    }
}

public static class MinimumPension
{
    /// <summary>The standard minimum drawdown percentage for an account-based pension by age at 1 July.</summary>
    public static decimal Factor(int age) => age switch
    {
        < 65 => 4m,
        < 75 => 5m,
        < 80 => 6m,
        < 85 => 7m,
        < 90 => 9m,
        < 95 => 11m,
        _ => 14m,
    };

    /// <summary>Opening balance times the factor, rounded to the nearest $10.</summary>
    public static decimal For(decimal openingBalance, int age) =>
        Math.Round(openingBalance * Factor(age) / 100m / 10m, MidpointRounding.AwayFromZero) * 10m;

    /// <summary>
    /// The first year's minimum for a pension started during the year: the balance on the start day times the factor
    /// for the member's age that day, times the days from the start day to 30 June (both counted) over the days in the
    /// year, rounded to the nearest $10. A pension started on or after 1 June has no minimum that year.
    /// </summary>
    public static decimal ProRata(decimal startBalance, int ageAtStart, DateOnly start, DateOnly yearStart, DateOnly yearEnd)
    {
        if (start <= yearStart)
            return For(startBalance, ageAtStart);
        if (start > yearEnd || (start.Month == yearEnd.Month && start.Year == yearEnd.Year))
            return 0;
        var days = yearEnd.DayNumber - start.DayNumber + 1;
        var daysInYear = yearEnd.DayNumber - yearStart.DayNumber + 1;
        return Math.Round(startBalance * Factor(ageAtStart) / 100m * days / daysInYear / 10m, MidpointRounding.AwayFromZero) * 10m;
    }

    /// <summary>The minimum for one retirement phase account in the year, or null when the member's age isn't known.</summary>
    public static decimal? ForAccount(SmsfMemberAccount account, SmsfMember member, DateOnly yearStart)
    {
        var yearEnd = yearStart.AddYears(1).AddDays(-1);
        if (account.StartedInYear(yearStart) && account.PensionStart is { } start)
            return member.AgeAt(start) is { } startAge
                ? ProRata(account.StartBalance ?? account.Opening + account.RolloverIn, startAge, start, yearStart, yearEnd)
                : null;
        return member.AgeAt(yearStart) is { } age ? For(account.Opening, age) : null;
    }
}

/// <summary>One account after earnings and tax.</summary>
public sealed record SmsfAccountResult(SmsfMemberAccount Account, decimal Earnings, decimal ContributionsTax, decimal IncomeTax)
{
    public decimal Closing => Account.Opening + Account.Inflows + Earnings - ContributionsTax - IncomeTax - Account.Outflows;
}

public sealed record SmsfMemberSummary(
    SmsfMember Member,
    IReadOnlyList<SmsfAccountResult> Accounts,
    int? Age,
    ContributionCaps Caps,
    decimal NonConcessionalLimit,
    decimal? MinimumPension)
{
    public decimal Opening => Accounts.Sum(a => a.Account.Opening);
    public decimal Closing => Accounts.Sum(a => a.Closing);
    public decimal Concessional => Accounts.Sum(a => a.Account.Concessional);
    public decimal NonConcessional => Accounts.Sum(a => a.Account.NonConcessional);
    public decimal PensionPaid => Accounts.Sum(a => a.Account.PensionPaid);
    public bool OverConcessionalCap => Concessional > Caps.Concessional;
    public bool OverNonConcessionalCap => NonConcessional > NonConcessionalLimit;
    public bool PensionShort => MinimumPension is { } min && PensionPaid < min;
}

/// <summary>
/// Shares the fund's year between the member accounts. Earnings (profit before tax, less the
/// contributions in revenue) go to every account by its weighted balance. Contributions tax (15%
/// of concessional contributions) is charged to the account that received them, and the rest of
/// the fund's tax to accumulation accounts by weighted balance, since pension earnings are exempt.
/// Rounding cents go to the last account so the totals agree with the fund.
/// </summary>
public static class SmsfAllocation
{
    public const decimal ContributionsTaxRate = 15m;

    public static IReadOnlyList<SmsfMemberSummary> Build(int year, SmsfYearData data, decimal earnings, decimal fundTax, DateOnly yearStart)
    {
        var accounts = data.Accounts;
        var contributionsTax = accounts.ToDictionary(a => a.MemberAccountId, a => TaxReconciliation.Round(a.Concessional * ContributionsTaxRate / 100m));
        var otherTax = fundTax - contributionsTax.Values.Sum();

        var earningShares = Split(earnings, accounts, _ => true);
        var taxed = accounts.Any(a => !a.IsPension) ? (Func<SmsfMemberAccount, bool>)(a => !a.IsPension) : _ => true;
        var taxShares = Split(otherTax, accounts, taxed);

        var caps = ContributionCaps.For(year);
        return data.Members.Select(m =>
        {
            var results = accounts.Where(a => a.MemberId == m.MemberId)
                .Select(a => new SmsfAccountResult(a, earningShares[a.MemberAccountId], contributionsTax[a.MemberAccountId], taxShares[a.MemberAccountId]))
                .ToList();
            var age = m.AgeAt(yearStart);
            var tsb = results.Sum(r => r.Account.Opening);
            var pension = results.Where(r => r.Account.IsPension).ToList();
            var minimums = pension.Select(r => MinimumPension.ForAccount(r.Account, m, yearStart)).ToList();
            decimal? minimum = pension.Count > 0 && minimums.All(x => x is not null) ? minimums.Sum(x => x!.Value) : null;
            return new SmsfMemberSummary(m, results, age, caps, caps.NonConcessionalLimit(tsb, age), minimum);
        }).ToList();
    }

    private static Dictionary<int, decimal> Split(decimal amount, IReadOnlyList<SmsfMemberAccount> accounts, Func<SmsfMemberAccount, bool> share)
    {
        var result = accounts.ToDictionary(a => a.MemberAccountId, _ => 0m);
        var sharing = accounts.Where(share).ToList();
        var total = sharing.Sum(a => a.Weighted);
        if (sharing.Count == 0)
            return result;
        foreach (var a in sharing)
            result[a.MemberAccountId] = total == 0
                ? TaxReconciliation.Round(amount / sharing.Count)
                : TaxReconciliation.Round(amount * a.Weighted / total);
        result[sharing[^1].MemberAccountId] += amount - sharing.Sum(a => result[a.MemberAccountId]);
        return result;
    }
}

/// <summary>Members and their yearly accounts (tblSmsfMember, tblSmsfMemberAccount).</summary>
public sealed class SmsfMemberService(IDbConnectionFactory db)
{
    public async Task<SmsfYearData> GetAsync(int portfolioId, int year)
    {
        await using var con = db.Create();
        await using var grid = await con.QueryMultipleAsync("uspGetSmsfMembers", new { iUserId = portfolioId, iYear = year },
            commandType: CommandType.StoredProcedure);
        var members = (await grid.ReadAsync()).Cast<IDictionary<string, object?>>().ToList();
        var accounts = grid.IsConsumed ? [] : (await grid.ReadAsync()).Cast<IDictionary<string, object?>>().ToList();
        return Map(members, accounts);
    }

    public static SmsfYearData Map(IEnumerable<IDictionary<string, object?>> members, IEnumerable<IDictionary<string, object?>> accounts) => new(
        members.Select(r => new SmsfMember(Db.Int(r, "iMemberID") ?? 0, Db.Str(r, "strName"), Db.Date(r, "dtBirthDate")))
            .Where(m => m.MemberId > 0).ToList(),
        accounts.Select(r => new SmsfMemberAccount(
                Db.Int(r, "iMemberAccountID") ?? 0,
                Db.Int(r, "iMemberID") ?? 0,
                Db.Int(r, "iYear") ?? 0,
                Db.Str(r, "strPhase") == SmsfPhases.Retirement ? SmsfPhases.Retirement : SmsfPhases.Accumulation,
                Db.Dec(r, "dblOpeningBalance") ?? 0,
                Db.Dec(r, "dblConcessional") ?? 0,
                Db.Dec(r, "dblNonConcessional") ?? 0,
                Db.Dec(r, "dblRolloverIn") ?? 0,
                Db.Dec(r, "dblPensionPaid") ?? 0,
                Db.Dec(r, "dblLumpSum") ?? 0,
                Db.Dec(r, "dblInsurance") ?? 0,
                r.ContainsKey("dtPensionStart") ? Db.Date(r, "dtPensionStart") : null,
                r.ContainsKey("dblStartBalance") ? Db.Dec(r, "dblStartBalance") : null))
            .Where(a => a.MemberAccountId > 0).ToList());

    public async Task<ProcResult> SaveMemberAsync(int portfolioId, SmsfMemberInput input)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync<(string? Result, int? MemberId)>("uspSaveSmsfMember", new
        {
            iUserId = portfolioId,
            iMemberID = input.MemberId,
            strName = input.Name?.Trim() ?? "",
            dtBirthDate = input.BirthDate?.Date,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.MemberId);
    }

    public async Task<ProcResult> DeleteMemberAsync(int portfolioId, int memberId)
    {
        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspDeleteSmsfMember", new { iUserId = portfolioId, iMemberID = memberId },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    public async Task<ProcResult> SaveAccountAsync(int portfolioId, int year, DateOnly yearEnd, SmsfAccountInput input)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync<(string? Result, int? MemberAccountId)>("uspSaveSmsfMemberAccount", new
        {
            iUserId = portfolioId,
            iYear = year,
            dtYearEnd = yearEnd.ToDateTime(TimeOnly.MinValue),
            iMemberAccountID = input.MemberAccountId,
            iMemberID = input.MemberId,
            strPhase = input.Phase,
            dblOpeningBalance = input.Opening,
            dblConcessional = input.Concessional,
            dblNonConcessional = input.NonConcessional,
            dblRolloverIn = input.RolloverIn,
            dblPensionPaid = input.PensionPaid,
            dblLumpSum = input.LumpSum,
            dblInsurance = input.Insurance,
            dtPensionStart = input.Phase == SmsfPhases.Retirement ? input.PensionStart?.Date : null,
            dblStartBalance = input.Phase == SmsfPhases.Retirement && input.PensionStart is not null ? input.StartBalance : null,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.MemberAccountId);
    }

    public async Task<ProcResult> DeleteAccountAsync(int portfolioId, int year, DateOnly yearEnd, int memberAccountId)
    {
        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspDeleteSmsfMemberAccount", new
        {
            iUserId = portfolioId, iYear = year, dtYearEnd = yearEnd.ToDateTime(TimeOnly.MinValue), iMemberAccountID = memberAccountId,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }
}

/// <summary>
/// The member data an actuary asks for to work out the exempt current pension income percentage: each member account's
/// balances and the year's flows, with pension start dates. Flows are the year's totals; the actuary asks for the
/// dates of large ones separately.
/// </summary>
public static class SmsfActuaryFile
{
    public static byte[] Build(string fundName, string? abn, string yearLabel, DateOnly yearStart, IReadOnlyList<SmsfMemberSummary> members)
    {
        static string C(string v) => LabelSummaryCsv.Csv(v);
        static string N(decimal v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Fund,{C(fundName)}");
        sb.AppendLine($"ABN,{C(abn ?? "")}");
        sb.AppendLine($"Financial year,{C(yearLabel)}");
        sb.AppendLine();
        sb.AppendLine("Member,Date of birth,Age at 1 July,Phase,Pension start,Balance at pension start,Opening balance,Concessional contributions," +
                      "Non-concessional contributions,Rollovers in,Pensions paid,Lump sums paid,Insurance premiums,Earnings allocated," +
                      "Contributions tax,Income tax,Closing balance,Minimum pension");
        foreach (var m in members)
            foreach (var r in m.Accounts)
            {
                var a = r.Account;
                var started = a.StartedInYear(yearStart);
                sb.AppendJoin(',', [
                    C(m.Member.Name), m.Member.BirthDate?.ToString("yyyy-MM-dd") ?? "", m.Age?.ToString() ?? "", a.IsPension ? "Retirement" : "Accumulation",
                    started ? a.PensionStart!.Value.ToString("yyyy-MM-dd") : "", started ? N(a.StartBalance ?? a.Opening + a.RolloverIn) : "",
                    N(a.Opening), N(a.Concessional), N(a.NonConcessional), N(a.RolloverIn), N(a.PensionPaid), N(a.LumpSum), N(a.Insurance),
                    N(r.Earnings), N(r.ContributionsTax), N(r.IncomeTax), N(r.Closing),
                    a.IsPension && MinimumPension.ForAccount(a, m.Member, yearStart) is { } min ? N(min) : "",
                ]);
                sb.AppendLine();
            }
        var all = members.SelectMany(m => m.Accounts).ToList();
        var weighted = all.Sum(r => r.Account.Weighted);
        var pension = all.Where(r => r.Account.IsPension).Sum(r => r.Account.Weighted);
        sb.AppendLine();
        sb.AppendLine($"Average balances in retirement phase (estimate only; the actuary's figure is the one to use),{(weighted == 0 ? "" : (pension / weighted * 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "%")}");
        return [.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes(sb.ToString())];
    }
}
