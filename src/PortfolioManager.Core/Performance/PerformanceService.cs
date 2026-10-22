using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Performance;

/// <summary>
/// Portfolio value history, time-weighted return and benchmark comparison.
/// <para>
/// The legacy procs (uspCalculateTWR, uspBackfillHistory, uspGetPeriodPerformanceSummary) value the
/// portfolio one day at a time and count every bank row as money added or taken out, including a
/// trade's own settlement, so buying shares shows as a gain. This reads the rows once and works the
/// figures out in <see cref="PerformanceCalculator"/> instead.
/// </para>
/// </summary>
public sealed class PerformanceService(IDbConnectionFactory db)
{
    private sealed record TradeRow(DateTime Date, string Exchange, string Code, string Type, double Quantity,
        decimal OrderTotal, double PricePerUnit, string? Currency, bool HasCash, bool IsReinvestment);

    private sealed record CashRow(DateTime Date, decimal Deposit, decimal Withdrawal, string? Currency, bool FromTrade);

    private sealed record PriceRow(string Exchange, string Code, DateTime Date, double Close);

    private sealed record SplitRow(string Exchange, string Code, DateTime Date, double Ratio);

    private sealed record IncomeRow(DateTime Date, decimal Amount);

    public async Task<PerformanceResult> GetAsync(int portfolioId, DateOnly from, DateOnly to)
    {
        await using var con = db.Create();
        var input = await ReadAsync(con, portfolioId, from, to);
        return PerformanceCalculator.Calculate(input, from, to);
    }

    /// <summary>Each holding's price gain, currency gain and income over the period (Reports > Contribution).</summary>
    public async Task<ContributionReport> GetContributionAsync(int portfolioId, DateOnly from, DateOnly to)
    {
        await using var con = db.Create();
        var input = await ReadAsync(con, portfolioId, from, to);
        // Reinvested dividends count too: the DRP purchase is money put in from that income
        var income = await con.QueryAsync<(string Exchange, string Code, DateTime Date, decimal Amount)>("""
            SELECT strStockExchangeCode, strStockCode, CAST(dtmTransactionDate AS date), CAST(ISNULL(dblNetAmount, ISNULL(dblTotal, 0)) AS decimal(19,4))
            FROM tblPayouts
            WHERE iUserID = @portfolioId AND strStockCode IS NOT NULL
              AND dtmTransactionDate >= @from AND dtmTransactionDate < DATEADD(DAY, 1, @to)
            """, new { portfolioId, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue) });
        return ContributionCalculator.Calculate(input,
            income.Select(i => new PerfHoldingIncome(i.Exchange ?? "", i.Code, D(i.Date), i.Amount)).ToList(), from, to);
    }

    private static async Task<PerformanceInput> ReadAsync(System.Data.Common.DbConnection con, int portfolioId, DateOnly from, DateOnly to)
    {
        var p = new { portfolioId, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue) };
        using var grid = await con.QueryMultipleAsync(Sql, p);

        var currency = (await grid.ReadAsync<string?>()).FirstOrDefault() ?? "AUD";
        var trades = (await grid.ReadAsync<TradeRow>()).Select(t => new PerfTrade(
            D(t.Date), t.Exchange, t.Code, t.Type, t.Quantity, t.OrderTotal, t.PricePerUnit, t.Currency ?? "", t.HasCash, t.IsReinvestment)).ToList();
        var cash = (await grid.ReadAsync<CashRow>()).Select(c => new PerfCash(
            D(c.Date), c.Deposit, c.Withdrawal, c.Currency ?? "", c.FromTrade)).ToList();
        var prices = (await grid.ReadAsync<PriceRow>()).Select(r => new PerfPrice(r.Exchange, r.Code, D(r.Date), r.Close)).ToList();
        var splits = (await grid.ReadAsync<SplitRow>()).Select(s => new PerfSplit(s.Exchange, s.Code, D(s.Date), s.Ratio)).ToList();
        var income = (await grid.ReadAsync<IncomeRow>()).Select(i => new PerfIncome(D(i.Date), i.Amount)).ToList();

        return new PerformanceInput(currency, trades, cash, prices, splits, income);
    }

    /// <summary>Market indexes with history on file, then the suggested ETFs that have prices.</summary>
    public async Task<IReadOnlyList<BenchmarkChoice>> GetChoicesAsync()
    {
        await using var con = db.Create();
        var indexes = await con.QueryAsync<string>(
            "SELECT DISTINCT strIndexCode FROM tblMarketIndexHistory ORDER BY strIndexCode");
        var etfCodes = Benchmark.SuggestedEtfs.Select(e => Benchmark.Parse(e.Key)!.Value.Code).ToArray();
        var withPrices = (await con.QueryAsync<string>(
            "SELECT DISTINCT strStockCode FROM tblSharePrice WHERE strStockExchangeCode = 'ASX' AND strStockCode IN @etfCodes",
            new { etfCodes })).Select(c => c.Trim().ToUpperInvariant()).ToHashSet();

        return indexes.Select(i => new BenchmarkChoice($"{Benchmark.IndexPrefix}:{i.Trim().ToUpperInvariant()}", $"{i.Trim()} index"))
            .Concat(Benchmark.SuggestedEtfs.Where(e => withPrices.Contains(Benchmark.Parse(e.Key)!.Value.Code)))
            .ToList();
    }

    /// <summary>The benchmark's values from a little before the period to its end. Empty when the key isn't usable.</summary>
    public async Task<BenchmarkResult?> GetBenchmarkAsync(string? key, IReadOnlyList<BenchmarkChoice> choices, DateOnly from, DateOnly to)
    {
        if (Benchmark.Parse(key) is not { } parsed)
            return null;
        var normalKey = $"{parsed.Exchange}:{parsed.Code}";
        var choice = choices.FirstOrDefault(c => c.Key == normalKey) ?? new BenchmarkChoice(normalKey, normalKey);

        var p = new { code = parsed.Code, exchange = parsed.Exchange, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue) };
        await using var con = db.Create();
        // Values inside the period plus the last one before it, to rebase on
        var rows = parsed.Exchange == Benchmark.IndexPrefix
            ? await con.QueryAsync<(DateTime Date, double Value)>("""
                SELECT dtmDate, dblIndexValue FROM tblMarketIndexHistory
                WHERE strIndexCode = @code AND dtmDate >= @from AND dtmDate <= @to
                UNION ALL
                SELECT * FROM (SELECT TOP 1 dtmDate, dblIndexValue FROM tblMarketIndexHistory
                WHERE strIndexCode = @code AND dtmDate < @from ORDER BY dtmDate DESC) B
                """, p)
            : await con.QueryAsync<(DateTime Date, double Value)>("""
                SELECT CAST(dtmDetailsAsAtDate AS date), dblClosePrice FROM tblSharePrice
                WHERE strStockExchangeCode = @exchange AND strStockCode = @code AND dblClosePrice > 0
                  AND dtmDetailsAsAtDate >= @from AND dtmDetailsAsAtDate <= @to
                UNION ALL
                SELECT * FROM (SELECT TOP 1 CAST(dtmDetailsAsAtDate AS date) AS d, dblClosePrice FROM tblSharePrice
                WHERE strStockExchangeCode = @exchange AND strStockCode = @code AND dblClosePrice > 0
                  AND dtmDetailsAsAtDate < @from ORDER BY dtmDetailsAsAtDate DESC) B
                """, p);

        return Benchmark.Compare(choice, rows.Select(r => new BenchmarkPoint(D(r.Date), r.Value)).ToList(), from, to);
    }

    private static DateOnly D(DateTime d) => DateOnly.FromDateTime(d);

    // Everything up to the end date, with prices from the day before the period plus the last one before that
    private const string Sql = """
        SELECT TOP 1 strPortfolioCurrency FROM tblUsers WHERE iUserId = @portfolioId;

        SELECT T.dtmDateOfTransaction AS Date, T.strStockExchangeCode AS Exchange, T.strStockCode AS Code, T.strType AS Type,
               ISNULL(T.iQuantity, 0) AS Quantity, CAST(ISNULL(T.dblOrderTotal, 0) AS decimal(19,4)) AS OrderTotal,
               ISNULL(T.dblPricePerUnit, 0) AS PricePerUnit, T.strCurrency AS Currency,
               CAST(CASE WHEN EXISTS (SELECT 1 FROM tblBankStatement B
                                      WHERE B.iUserId = T.iUserId
                                        AND (B.iShareTradeId = T.iShareTradeId OR B.iBankStatementId = T.iBankStatementId))
                         THEN 1 ELSE 0 END AS bit) AS HasCash,
               CAST(CASE WHEN EXISTS (SELECT 1 FROM tblPayouts P WHERE P.iUserID = T.iUserId AND P.iReinvestmentTradeID = T.iShareTradeId)
                         THEN 1 ELSE 0 END AS bit) AS IsReinvestment
        FROM tblShareTrades T
        WHERE T.iUserId = @portfolioId AND T.dtmDateOfTransaction <= @to
        ORDER BY T.dtmDateOfTransaction, T.iShareTradeId;

        SELECT B.dtmDateOfTransaction AS Date, CAST(ISNULL(B.dblDepositAmount, 0) AS decimal(19,4)) AS Deposit,
               CAST(ISNULL(B.dblWithdrawalAmount, 0) AS decimal(19,4)) AS Withdrawal, B.strCurrency AS Currency,
               CAST(CASE WHEN B.iShareTradeId IS NOT NULL
                           OR EXISTS (SELECT 1 FROM tblShareTrades T WHERE T.iUserId = B.iUserId AND T.iBankStatementId = B.iBankStatementId)
                         THEN 1 ELSE 0 END AS bit) AS FromTrade
        FROM tblBankStatement B
        WHERE B.iUserId = @portfolioId AND B.dtmDateOfTransaction IS NOT NULL AND B.dtmDateOfTransaction <= @to
        ORDER BY B.dtmDateOfTransaction, B.iBankStatementId;

        DECLARE @ws date = DATEADD(DAY, -1, @from);
        DECLARE @ccy varchar(50) = (SELECT TOP 1 strPortfolioCurrency FROM tblUsers WHERE iUserId = @portfolioId);
        WITH Wanted AS (
            SELECT DISTINCT strStockExchangeCode AS Exchange, strStockCode AS Code
            FROM tblShareTrades WHERE iUserId = @portfolioId AND dtmDateOfTransaction <= @to
            UNION
            SELECT DISTINCT strStockExchangeCode, strStockCode FROM tblSharePrice
            WHERE strStockExchangeCode = 'FOREX' AND REPLACE(strStockCode, '/', '') LIKE '%' + @ccy + '%'
        )
        SELECT P.strStockExchangeCode AS Exchange, P.strStockCode AS Code, CAST(P.dtmDetailsAsAtDate AS date) AS Date, P.dblClosePrice AS [Close]
        FROM tblSharePrice P
        JOIN Wanted W ON W.Exchange = P.strStockExchangeCode AND W.Code = P.strStockCode
        WHERE P.dblClosePrice > 0 AND P.dtmDetailsAsAtDate >= @ws AND P.dtmDetailsAsAtDate <= @to
          AND (P.iUserID IS NULL OR P.iUserID = 0 OR P.iUserID = @portfolioId)
        UNION ALL
        SELECT L.Exchange, L.Code, L.Date, L.[Close]
        FROM Wanted W
        CROSS APPLY (SELECT TOP 1 P.strStockExchangeCode AS Exchange, P.strStockCode AS Code,
                            CAST(P.dtmDetailsAsAtDate AS date) AS Date, P.dblClosePrice AS [Close]
                     FROM tblSharePrice P
                     WHERE P.strStockExchangeCode = W.Exchange AND P.strStockCode = W.Code
                       AND P.dblClosePrice > 0 AND P.dtmDetailsAsAtDate < @ws
                       AND (P.iUserID IS NULL OR P.iUserID = 0 OR P.iUserID = @portfolioId)
                     ORDER BY P.dtmDetailsAsAtDate DESC) L;

        SELECT strStockExchangeCode AS Exchange, strStockCode AS Code, dtmActionDate AS Date, dblRatio AS Ratio
        FROM tblCorporateActions
        WHERE iUserID = @portfolioId AND strActionType IN ('SPLIT', 'CONSOLIDATION') AND dblRatio > 0;

        SELECT CAST(dtmTransactionDate AS date) AS Date, CAST(ISNULL(dblNetAmount, ISNULL(dblTotal, 0)) AS decimal(19,4)) AS Amount
        FROM tblPayouts
        WHERE iUserID = @portfolioId AND iReinvestmentTradeID IS NULL
          AND dtmTransactionDate >= @from AND dtmTransactionDate < DATEADD(DAY, 1, @to);
        """;
}
