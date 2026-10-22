using System.Data;
using Dapper;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.Accounting;

/// <summary>
/// One holding at the revaluation date. Cost is what its "- Cost Base" account holds in the ledger;
/// Price is the closing price on file (or typed in), null when there is none. A cost with no units
/// at the date (the unrealised gains proc doesn't list it) is left at cost rather than written off.
/// </summary>
public sealed record RevaluationHolding(string ExchangeCode, string StockCode, decimal Units, decimal? Price, decimal Cost)
{
    public string Key => $"{StockCode}.{ExchangeCode}";
    public bool LeftAtCost => Units == 0;
    public decimal? MarketValue => LeftAtCost ? Cost : Price is { } p ? Math.Round(Units * p, 2) : null;
    public decimal? Adjustment => MarketValue is { } mv ? mv - Cost : null;

    /// <summary>The line's text on the journal, so the working is kept with it.</summary>
    public string Description =>
        $"{Key}: {Units:#,##0.######} units at {Price:#,##0.00####} = {MarketValue:#,##0.00} market value, less cost {Cost:#,##0.00}";
}

/// <summary>What the revaluation would post, before it is posted.</summary>
public sealed record RevaluationPlan(
    DateOnly Date,
    IReadOnlyList<RevaluationHolding> Holdings,
    decimal PriorAdjustment,
    DateOnly? Posted,
    DateOnly? Later)
{
    public bool MissingPrices => Holdings.Any(h => h.MarketValue is null);
    public decimal MarketValue => Holdings.Sum(h => h.MarketValue ?? 0);
    public decimal Cost => Holdings.Sum(h => h.Cost);
    public decimal TargetAdjustment => Holdings.Sum(h => h.Adjustment ?? 0);

    /// <summary>The unrealised gain (or fall) this revaluation posts.</summary>
    public decimal Movement => TargetAdjustment - PriorAdjustment;

    public static decimal DeferredTax(decimal movement, decimal ratePercent) =>
        Math.Round(movement * ratePercent / 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Joins the ledger's cost base balances with the units and prices the unrealised gains proc
    /// reports at the date. Typed prices win over the ones on file.
    /// </summary>
    public static IReadOnlyList<RevaluationHolding> Combine(
        IReadOnlyDictionary<string, decimal> ledgerCosts,
        IEnumerable<UnrealisedGainLine> open,
        IReadOnlyDictionary<string, decimal>? prices = null)
    {
        var byKey = new Dictionary<string, RevaluationHolding>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in open.Where(l => l.Units != 0))
        {
            var h = new RevaluationHolding(l.ExchangeCode, l.StockCode, l.Units, l.MarketPrice, 0);
            byKey[h.Key] = h;
        }
        foreach (var (account, cost) in ledgerCosts)
        {
            if (cost == 0 && !byKey.ContainsKey(account))
                continue;
            if (byKey.TryGetValue(account, out var h))
                byKey[account] = h with { Cost = cost };
            else
            {
                var dot = account.LastIndexOf('.');
                byKey[account] = new RevaluationHolding(dot > 0 ? account[(dot + 1)..] : "", dot > 0 ? account[..dot] : account, 0, null, cost);
            }
        }
        if (prices is not null)
            foreach (var (key, price) in prices)
                if (byKey.TryGetValue(key, out var h) && h.Units != 0)
                    byKey[key] = h with { Price = price };

        return byKey.Values
            .OrderBy(h => h.StockCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.ExchangeCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed record RevaluationResult(string? Error, decimal Movement = 0, decimal DeferredTax = 0)
{
    public bool Ok => Error is null;
}

/// <summary>
/// The year-end journal that brings investments to market value. Each holding's market value less
/// its ledger cost goes to 1390 Investments - Market Value Adjustment, the adjustment already there is
/// reversed, and the movement goes to 4600 Unrealised Gains on Investments, with optional deferred tax.
/// Posting again for the same date replaces the journal.
/// </summary>
public sealed class RevaluationService(
    IDbConnectionFactory db, ChartOfAccountsService accounts, UnrealisedGainsService unrealised, RealisedGainService gains)
{
    public const string SourceModule = "Revaluation";
    public const int AdjustmentAccount = 1390;

    public async Task<RevaluationPlan> PlanAsync(int portfolioId, DateOnly date, IReadOnlyDictionary<string, decimal>? prices = null)
    {
        // Sale journals move cost out of the cost base accounts, so they must be current first
        await gains.SyncAsync(portfolioId);
        await using var con = db.Create();
        var day = date.ToDateTime(TimeOnly.MinValue);

        var costs = (await con.QueryAsync<(string Account, decimal Cost)>("""
            SELECT LEFT(A.strAccountName, LEN(A.strAccountName) - LEN(' - Cost Base')) AS Account,
                   SUM(ISNULL(L.dblDebit, 0) - ISNULL(L.dblCredit, 0)) AS Cost
            FROM tblJournalLines L
            JOIN tblJournal J ON J.iJournalID = L.iJournalID
            JOIN tblChartOfAccounts A ON A.iAccountID = L.iAccountID
            WHERE J.iUserID = @portfolioId AND J.dtmTransactionDate <= @day AND A.strAccountName LIKE '% - Cost Base'
            GROUP BY A.strAccountName
            """, new { portfolioId, day }))
            .GroupBy(r => r.Account.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Cost), StringComparer.OrdinalIgnoreCase);

        var open = await unrealised.GetAsync(portfolioId, date, groupByStock: true);

        var journals = (await con.QueryAsync<(DateTime Date, decimal Adjustment)>("""
            SELECT J.dtmTransactionDate AS Date,
                   ISNULL((SELECT SUM(ISNULL(L.dblDebit, 0) - ISNULL(L.dblCredit, 0))
                           FROM tblJournalLines L JOIN tblChartOfAccounts A ON A.iAccountID = L.iAccountID
                           WHERE L.iJournalID = J.iJournalID AND A.iAccountNumber = @account), 0) AS Adjustment
            FROM tblJournal J
            WHERE J.iUserID = @portfolioId AND J.strSourceModule = @module
            """, new { portfolioId, module = SourceModule, account = AdjustmentAccount }))
            .Select(r => (Date: DateOnly.FromDateTime(r.Date), r.Adjustment))
            .ToList();

        // The adjustment earlier revaluations (and any manual journals to 1390) left at the date
        var prior = await con.ExecuteScalarAsync<decimal?>("""
            SELECT SUM(ISNULL(L.dblDebit, 0) - ISNULL(L.dblCredit, 0))
            FROM tblJournalLines L
            JOIN tblJournal J ON J.iJournalID = L.iJournalID
            JOIN tblChartOfAccounts A ON A.iAccountID = L.iAccountID
            WHERE J.iUserID = @portfolioId AND J.dtmTransactionDate <= @day AND A.iAccountNumber = @account
              AND (A.iUserID = @portfolioId OR A.iUserID = 0)
              AND NOT (J.strSourceModule = @module AND J.dtmTransactionDate = @day)
            """, new { portfolioId, day, account = AdjustmentAccount, module = SourceModule }) ?? 0;

        return new RevaluationPlan(
            date,
            RevaluationPlan.Combine(costs, open.Lines, prices),
            prior,
            journals.Any(j => j.Date == date) ? date : null,
            journals.Where(j => j.Date > date).Select(j => (DateOnly?)j.Date).Min());
    }

    /// <summary>
    /// Posts the revaluation for the date in one transaction: uspStartRevaluation, a line per
    /// holding with uspAddJournalLine, then uspFinishRevaluation for the reversal, movement and tax.
    /// </summary>
    public async Task<RevaluationResult> PostAsync(int portfolioId, DateOnly date, decimal taxRatePercent, IReadOnlyDictionary<string, decimal>? prices = null)
    {
        if (!AccountingMode.PostsJournals(await accounts.GetAccountingModeAsync(portfolioId)))
            return new RevaluationResult("This portfolio uses single-entry accounting, so there is no ledger to revalue.");
        if (taxRatePercent is < 0 or >= 100)
            return new RevaluationResult("The tax rate must be between 0% and 100%.");

        var plan = await PlanAsync(portfolioId, date, prices);
        if (plan.Later is { } later)
            return new RevaluationResult($"There is a revaluation at {later:d MMM yyyy}, which reverses this one. Remove it first, then post this one and post that one again.");
        if (plan.MissingPrices)
            return new RevaluationResult("Some holdings have no price at that date. Enter a price for each one.");
        if (plan.Holdings.Count == 0 && plan.PriorAdjustment == 0)
            return new RevaluationResult("There are no investments to revalue at that date.");

        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();

        var start = await con.QuerySingleAsync<(int JournalId, int AdjustmentAccountId)>("uspStartRevaluation", new
        {
            iUserId = portfolioId,
            dtDate = date.ToDateTime(TimeOnly.MinValue),
            strDescription = $"Market value revaluation at {date:d MMM yyyy}",
        }, tx, commandType: CommandType.StoredProcedure);

        foreach (var h in plan.Holdings.Where(h => h.Adjustment is not null and not 0m))
        {
            var adj = h.Adjustment!.Value;
            await con.ExecuteAsync("uspAddJournalLine", new
            {
                iJournalID = start.JournalId,
                iAccountID = start.AdjustmentAccountId,
                dblDebit = adj > 0 ? adj : 0,
                dblCredit = adj < 0 ? -adj : 0,
                strDescription = h.Description.Length > 500 ? h.Description[..500] : h.Description,
            }, tx, commandType: CommandType.StoredProcedure);
        }

        var done = await con.QuerySingleAsync<(decimal Movement, decimal DeferredTax)>("uspFinishRevaluation", new
        {
            iUserId = portfolioId,
            iJournalID = start.JournalId,
            dblTaxRate = taxRatePercent / 100m,
        }, tx, commandType: CommandType.StoredProcedure);

        await tx.CommitAsync();
        return new RevaluationResult(null, done.Movement, done.DeferredTax);
    }

    public async Task<ProcResult> DeleteAsync(int portfolioId, DateOnly date)
    {
        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspDeleteRevaluation",
            new { iUserId = portfolioId, dtDate = date.ToDateTime(TimeOnly.MinValue) }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    /// <summary>A starting tax rate: 30% for a company (a passive investment company isn't a base rate entity), otherwise none.</summary>
    public static decimal DefaultTaxRate(string entityType) => entityType == Clients.ClientEntityTypes.Company ? 30m : 0m;
}
