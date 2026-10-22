using System.Data;
using Dapper;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Payouts;

namespace PortfolioManager.Core.CorporateActions;

/// <summary>How a suggested dividend is split into franked and unfranked, and where that came from.</summary>
public sealed record FrankingGuess(decimal FrankedShare, decimal CreditPerFrankedDollar, string Basis)
{
    /// <summary>The credit on a fully franked dollar at the 30% company rate: 30/70.</summary>
    public const decimal StandardCreditRate = 30m / 70m;
}

/// <summary>An announced dividend on units this portfolio held at the ex-date, with no payout recorded for it yet.</summary>
public sealed record DividendSuggestion(
    int Id, string ExchangeCode, string StockCode, DateOnly ExDate, DateOnly? PaymentDate, decimal AmountPerUnit,
    string? Currency, string? Period, decimal Units, FrankingGuess? Franking, bool LastWasDrp, string Source)
{
    public DateOnly PayDate => PaymentDate ?? ExDate;

    public decimal Total => Math.Round(Units * AmountPerUnit, 2, MidpointRounding.AwayFromZero);

    public bool IsAustralian => ExchangeCode.Equals("ASX", StringComparison.OrdinalIgnoreCase);
}

/// <summary>An announced split or consolidation of a holding this portfolio had the day before, not yet recorded.</summary>
public sealed record SplitSuggestion(int Id, string ExchangeCode, string StockCode, DateOnly EffectiveDate, int NewUnits, int OldUnits, decimal UnitsBefore, string Source)
{
    public SplitRatio Ratio => new(NewUnits, OldUnits);
    public decimal UnitsAfter => UnitsBefore * Ratio.Factor;
}

public sealed record AnnouncedSuggestions(IReadOnlyList<DividendSuggestion> Dividends, IReadOnlyList<SplitSuggestion> Splits)
{
    public int Count => Dividends.Count + Splits.Count;
}

/// <summary>The pure rules: franking from the last payout, the payout to add, and whether one click is safe.</summary>
public static class AnnouncedPayouts
{
    /// <summary>
    /// The last payout's mix of franked and unfranked, and its credit per franked dollar (30/70 when it had none).
    /// Null when there was no earlier dividend to go by.
    /// </summary>
    public static FrankingGuess? FromLastPayout(decimal? franked, decimal? unfranked, decimal? credit)
    {
        var f = franked ?? 0;
        var u = unfranked ?? 0;
        if (f < 0 || u < 0 || f + u <= 0)
            return null;
        var share = Math.Round(f / (f + u), 4);
        var rate = f > 0 && credit is > 0 ? Math.Round(credit.Value / f, 6) : FrankingGuess.StandardCreditRate;
        var basis = share switch
        {
            1m => "fully franked, like the last payout",
            0m => "unfranked, like the last payout",
            _ => $"{share:P0} franked, like the last payout",
        };
        return new FrankingGuess(share, rate, basis);
    }

    /// <summary>
    /// Why the dividend can't be added with one click, or null when it can: a different currency needs converting,
    /// and an ASX dividend needs its franking known.
    /// </summary>
    public static string? NeedsReview(DividendSuggestion d, string portfolioCurrency)
    {
        if (d.Currency is { Length: > 0 } c && !c.Equals(portfolioCurrency, StringComparison.OrdinalIgnoreCase))
            return $"Paid in {c}: enter the {portfolioCurrency} amount received.";
        if (d.IsAustralian && d.Franking is null)
            return "Franking isn't known: check the dividend statement.";
        if (d.LastWasDrp)
            return "The last dividend was reinvested: record it as a DRP if it was again.";
        return null;
    }

    /// <summary>The payout to add: the total split by the franking guess (unfranked when there is none), dated on the payment date.</summary>
    public static PayoutInput ToPayout(DividendSuggestion d)
    {
        var total = d.Total;
        var franked = d.Franking is null ? 0 : Math.Round(total * d.Franking.FrankedShare, 2, MidpointRounding.AwayFromZero);
        var credit = d.Franking is null ? 0 : Math.Round(franked * d.Franking.CreditPerFrankedDollar, 2, MidpointRounding.AwayFromZero);
        var period = string.IsNullOrWhiteSpace(d.Period) ? "" : d.Period.Trim() + " ";
        return new PayoutInput
        {
            StockExchangeCode = d.ExchangeCode,
            StockCode = d.StockCode,
            Date = d.PayDate.ToDateTime(TimeOnly.MinValue),
            Total = total,
            Franked = franked,
            Unfranked = total - franked,
            FrankingCredit = credit,
            Comment = $"{period}dividend {d.AmountPerUnit:0.######} a unit on {d.Units:0.####} units, ex {d.ExDate:d MMM yyyy} (announced, {d.Source})",
            Confirmed = true,
        };
    }

    /// <summary>Units held before the ex-date, in that day's units: today's trades are in post-split units, so divide by later splits.</summary>
    public static decimal UnitsOnTheDay(decimal unitsInTodaysTerms, decimal laterSplitFactor) =>
        laterSplitFactor > 0 ? Math.Round(unitsInTodaysTerms / laterSplitFactor, 6) : unitsInTodaysTerms;
}

/// <summary>
/// Dividends and splits the price updater saved from the provider, matched against one portfolio's holdings.
/// Confirming one adds it through the usual procs (uspAddPayout, uspApplyStockSplit).
/// </summary>
public sealed class AnnouncedActionsService(IDbConnectionFactory db, PayoutService payouts, CorporateActionsService actions)
{
    /// <summary>How far back announcements are offered.</summary>
    public const int DaysBack = 400;

    private sealed record DividendRow(
        int Id, string ExchangeCode, string StockCode, DateTime ExDate, DateTime? PaymentDate, double AmountPerUnit,
        string? Currency, string? Period, string Source, double? Units, double? LaterFactor,
        double? LastFranked, double? LastUnfranked, double? LastCredit, int? LastDrpTradeId);

    private sealed record SplitRow(int Id, string ExchangeCode, string StockCode, DateTime EffectiveDate, int NewUnits, int OldUnits, string Source, double? Units);

    public async Task<AnnouncedSuggestions> GetAsync(int portfolioId, DateOnly today, int? onlyDividendId = null, int? onlySplitId = null)
    {
        await using var con = db.Create();
        var args = new { portfolioId, today = today.ToDateTime(TimeOnly.MinValue), since = today.AddDays(-DaysBack).ToDateTime(TimeOnly.MinValue), onlyDividendId, onlySplitId };

        // Paid already (or ex-date passed when there's no payment date), held at the ex-date, no payout near it, not dismissed
        var dividends = await con.QueryAsync<DividendRow>("""
            SELECT D.iAnnouncedDividendID AS Id, D.strStockExchangeCode AS ExchangeCode, D.strStockCode AS StockCode,
                   CAST(D.dtmExDate AS datetime) AS ExDate, CAST(D.dtmPaymentDate AS datetime) AS PaymentDate,
                   D.dblAmountPerUnit AS AmountPerUnit, D.strCurrency AS Currency, D.strPeriod AS Period, D.strSource AS Source,
                   (SELECT SUM(CASE WHEN UPPER(T.strType) = 'BUY' THEN T.iQuantity WHEN UPPER(T.strType) = 'SELL' THEN -T.iQuantity ELSE 0 END)
                      FROM tblShareTrades T
                     WHERE T.iUserId = @portfolioId AND T.strStockExchangeCode = D.strStockExchangeCode
                       AND T.strStockCode = D.strStockCode AND T.dtmDateOfTransaction < D.dtmExDate) AS Units,
                   (SELECT EXP(SUM(LOG(A.dblRatio))) FROM tblCorporateActions A
                     WHERE A.iUserID = @portfolioId AND A.strStockExchangeCode = D.strStockExchangeCode AND A.strStockCode = D.strStockCode
                       AND A.strActionType IN ('SPLIT', 'CONSOLIDATION') AND A.dblRatio > 0 AND A.dtmActionDate > D.dtmExDate) AS LaterFactor,
                   L.dblFrankedAmount AS LastFranked, L.dblUnFrankedAmount AS LastUnfranked, L.dblFrankingCredit AS LastCredit,
                   L.iReinvestmentTradeID AS LastDrpTradeId
            FROM tblAnnouncedDividends D
            OUTER APPLY (SELECT TOP 1 P.dblFrankedAmount, P.dblUnFrankedAmount, P.dblFrankingCredit, P.iReinvestmentTradeID
                           FROM tblPayouts P
                          WHERE P.iUserID = @portfolioId AND P.strStockExchangeCode = D.strStockExchangeCode AND P.strStockCode = D.strStockCode
                            AND P.dtmTransactionDate < D.dtmExDate
                            AND ISNULL(P.dblFrankedAmount, 0) + ISNULL(P.dblUnFrankedAmount, 0) > 0
                          ORDER BY P.dtmTransactionDate DESC) L
            WHERE (@onlyDividendId IS NULL OR D.iAnnouncedDividendID = @onlyDividendId)
              AND D.dtmExDate >= @since AND ISNULL(D.dtmPaymentDate, D.dtmExDate) <= @today
              AND EXISTS (SELECT 1 FROM tblShareTrades T WHERE T.iUserId = @portfolioId
                            AND T.strStockExchangeCode = D.strStockExchangeCode AND T.strStockCode = D.strStockCode)
              AND NOT EXISTS (SELECT 1 FROM tblPayouts P WHERE P.iUserID = @portfolioId
                                AND P.strStockExchangeCode = D.strStockExchangeCode AND P.strStockCode = D.strStockCode
                                AND P.dtmTransactionDate >= DATEADD(day, -3, D.dtmExDate)
                                AND P.dtmTransactionDate <= DATEADD(day, 45, ISNULL(D.dtmPaymentDate, D.dtmExDate)))
              AND NOT EXISTS (SELECT 1 FROM tblAnnouncedActionDismissals X WHERE X.iUserID = @portfolioId
                                AND X.strKind = 'Dividend' AND X.iAnnouncedID = D.iAnnouncedDividendID)
            ORDER BY ISNULL(D.dtmPaymentDate, D.dtmExDate) DESC, D.strStockCode
            """, args);

        // Held the day before, no split recorded within a week of it, not dismissed; only the last year (older ones are usually in the trades already)
        var splits = await con.QueryAsync<SplitRow>("""
            SELECT S.iAnnouncedSplitID AS Id, S.strStockExchangeCode AS ExchangeCode, S.strStockCode AS StockCode,
                   CAST(S.dtmEffectiveDate AS datetime) AS EffectiveDate, S.iNewUnits AS NewUnits, S.iOldUnits AS OldUnits, S.strSource AS Source,
                   (SELECT SUM(CASE WHEN UPPER(T.strType) = 'BUY' THEN T.iQuantity WHEN UPPER(T.strType) = 'SELL' THEN -T.iQuantity ELSE 0 END)
                      FROM tblShareTrades T
                     WHERE T.iUserId = @portfolioId AND T.strStockExchangeCode = S.strStockExchangeCode
                       AND T.strStockCode = S.strStockCode AND T.dtmDateOfTransaction < S.dtmEffectiveDate) AS Units
            FROM tblAnnouncedSplits S
            WHERE (@onlySplitId IS NULL OR S.iAnnouncedSplitID = @onlySplitId)
              AND S.dtmEffectiveDate >= DATEADD(day, -365, @today) AND S.dtmEffectiveDate <= @today
              AND NOT EXISTS (SELECT 1 FROM tblCorporateActions A WHERE A.iUserID = @portfolioId
                                AND A.strStockExchangeCode = S.strStockExchangeCode AND A.strStockCode = S.strStockCode
                                AND A.strActionType IN ('SPLIT', 'CONSOLIDATION')
                                AND A.dtmActionDate BETWEEN DATEADD(day, -7, S.dtmEffectiveDate) AND DATEADD(day, 7, S.dtmEffectiveDate))
              AND NOT EXISTS (SELECT 1 FROM tblAnnouncedActionDismissals X WHERE X.iUserID = @portfolioId
                                AND X.strKind = 'Split' AND X.iAnnouncedID = S.iAnnouncedSplitID)
            ORDER BY S.dtmEffectiveDate DESC
            """, args);

        return new AnnouncedSuggestions(
            dividends
                .Select(r => new DividendSuggestion(r.Id, r.ExchangeCode, r.StockCode, DateOnly.FromDateTime(r.ExDate),
                    r.PaymentDate is { } p ? DateOnly.FromDateTime(p) : null, (decimal)r.AmountPerUnit, r.Currency, r.Period,
                    AnnouncedPayouts.UnitsOnTheDay((decimal)(r.Units ?? 0), (decimal)(r.LaterFactor ?? 1)),
                    AnnouncedPayouts.FromLastPayout((decimal?)r.LastFranked, (decimal?)r.LastUnfranked, (decimal?)r.LastCredit),
                    r.LastDrpTradeId is > 0, r.Source))
                .Where(d => d.Units > 0.0001m && d.Total > 0)
                .ToList(),
            splits
                .Where(r => r.Units > 0.0001 && r.NewUnits > 0 && r.OldUnits > 0)
                .Select(r => new SplitSuggestion(r.Id, r.ExchangeCode, r.StockCode, DateOnly.FromDateTime(r.EffectiveDate), r.NewUnits, r.OldUnits, (decimal)r.Units!.Value, r.Source))
                .ToList());
    }

    /// <summary>Adds the payout for one suggestion. Null when it's no longer offered (recorded, dismissed or not held).</summary>
    public async Task<PayoutInput?> ConfirmDividendAsync(int portfolioId, int id, DateOnly today)
    {
        var d = (await GetAsync(portfolioId, today, onlyDividendId: id, onlySplitId: -1)).Dividends.FirstOrDefault();
        if (d is null)
            return null;
        var payout = AnnouncedPayouts.ToPayout(d);
        await payouts.AddAsync(portfolioId, payout);
        return payout;
    }

    /// <summary>Records one suggested split. Null when it's no longer offered.</summary>
    public async Task<(SplitSuggestion Split, ProcResult Result)?> ApplySplitAsync(int portfolioId, int id, DateOnly today)
    {
        var s = (await GetAsync(portfolioId, today, onlyDividendId: -1, onlySplitId: id)).Splits.FirstOrDefault();
        if (s is null)
            return null;
        return (s, await actions.ApplySplitAsync(portfolioId, new NewSplit(s.ExchangeCode, s.StockCode, s.EffectiveDate, s.Ratio)));
    }

    public async Task<DividendSuggestion?> GetDividendAsync(int portfolioId, int id, DateOnly today) =>
        (await GetAsync(portfolioId, today, onlyDividendId: id, onlySplitId: -1)).Dividends.FirstOrDefault();

    /// <summary>Stops offering an announcement to this portfolio.</summary>
    public async Task DismissAsync(int portfolioId, string kind, int id)
    {
        if (kind is not ("Dividend" or "Split"))
            throw new ArgumentException("Kind must be Dividend or Split.", nameof(kind));
        await using var con = db.Create();
        await con.ExecuteAsync("uspDismissAnnouncedAction", new { iUserID = portfolioId, strKind = kind, iAnnouncedID = id },
            commandType: CommandType.StoredProcedure);
    }
}
