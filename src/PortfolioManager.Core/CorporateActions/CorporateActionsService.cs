using System.Data;
using Dapper;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.CorporateActions;

/// <summary>
/// A split or consolidation from tblCorporateActions. TradesAdjusted is how many trades it changed
/// (none noted for one recorded before the corporate actions migration, which can't be undone);
/// TradesNotAdjusted counts trades dated before it but added afterwards, so still in the old units.
/// </summary>
public sealed record SplitRecord(
    int ActionId,
    string ExchangeCode,
    string StockCode,
    DateTime Date,
    string ActionType,
    double Ratio,
    DateTime Applied,
    int TradesAdjusted,
    int TradesNotAdjusted)
{
    public bool CanUndo => TradesAdjusted > 0;

    public SplitRatio? RatioParts => SplitRatio.FromFactor(Ratio);

    public string RatioText => RatioParts?.ToString() ?? $"{Ratio:0.######} for 1";
}

public sealed record CapitalReturnRecord(
    int ReturnId,
    string ExchangeCode,
    string StockCode,
    DateTime Date,
    double? PerUnit,
    decimal Total,
    string? Comment);

/// <summary>Share splits, consolidations and returns of capital, through the procs in the corporate actions migration.</summary>
public sealed class CorporateActionsService(IDbConnectionFactory db)
{
    public async Task<IReadOnlyList<SplitRecord>> GetSplitsAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<SplitRecord>("""
            SELECT A.iActionID AS ActionId, A.strStockExchangeCode AS ExchangeCode, A.strStockCode AS StockCode,
                   CAST(A.dtmActionDate AS datetime) AS Date, A.strActionType AS ActionType, A.dblRatio AS Ratio,
                   A.dtmDateApplied AS Applied,
                   (SELECT COUNT(*) FROM tblCorporateActionTrades L WHERE L.iActionID = A.iActionID) AS TradesAdjusted,
                   (SELECT COUNT(*) FROM tblShareTrades T
                     WHERE T.iUserId = A.iUserID AND T.strStockExchangeCode = A.strStockExchangeCode
                       AND T.strStockCode = A.strStockCode AND T.dtmDateOfTransaction < A.dtmActionDate
                       AND EXISTS (SELECT 1 FROM tblCorporateActionTrades L WHERE L.iActionID = A.iActionID)
                       AND NOT EXISTS (SELECT 1 FROM tblCorporateActionTrades L
                                       WHERE L.iActionID = A.iActionID AND L.iShareTradeId = T.iShareTradeId)) AS TradesNotAdjusted
            FROM tblCorporateActions A
            WHERE A.iUserID = @portfolioId AND A.strActionType IN ('SPLIT', 'CONSOLIDATION')
            ORDER BY A.dtmActionDate DESC, A.iActionID DESC
            """, new { portfolioId });
        return rows.ToList();
    }

    /// <summary>The split factors recorded for a holding after a date, to turn today's units back into that day's.</summary>
    public async Task<IReadOnlyList<decimal>> GetSplitFactorsAfterAsync(int portfolioId, string market, string code, DateOnly date)
    {
        await using var con = db.Create();
        var factors = await con.QueryAsync<double>("""
            SELECT dblRatio FROM tblCorporateActions
            WHERE iUserID = @portfolioId AND strStockExchangeCode = @market AND strStockCode = @code
              AND strActionType IN ('SPLIT', 'CONSOLIDATION') AND dtmActionDate > @date AND dblRatio > 0
            """, new { portfolioId, market, code, date = date.ToDateTime(TimeOnly.MinValue) });
        return factors.Select(f => SplitRatio.FromFactor(f)?.Factor ?? (decimal)f).ToList();
    }

    /// <summary>Records a split with uspApplyStockSplit, which rewrites the earlier trades in the new units.</summary>
    public async Task<ProcResult> ApplySplitAsync(int portfolioId, NewSplit split)
    {
        await using var con = db.Create();
        var row = (IDictionary<string, object?>)await con.QuerySingleAsync("uspApplyStockSplit", new
        {
            iUserId = portfolioId,
            strStockExchangeCode = split.Market,
            strStockCode = split.Code,
            dtmSplitDate = split.Date.ToDateTime(TimeOnly.MinValue),
            dblSplitRatio = (double)split.Ratio.Factor,
            iNewUnits = split.Ratio.NewUnits,
            iOldUnits = split.Ratio.OldUnits,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(Text(row, "Result"), "Split Applied", Int(row, "iActionID"));
    }

    /// <summary>Undoes and deletes a split with uspDeleteStockSplit. Null when it isn't one of this portfolio's.</summary>
    public async Task<ProcResult?> DeleteSplitAsync(int portfolioId, int actionId)
    {
        await using var con = db.Create();
        var owned = await con.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tblCorporateActions WHERE iActionID = @actionId AND iUserID = @portfolioId AND strActionType IN ('SPLIT', 'CONSOLIDATION')",
            new { portfolioId, actionId });
        if (owned == 0)
            return null;

        // The proc checks the portfolio again
        var result = await con.ExecuteScalarAsync<string>("uspDeleteStockSplit",
            new { iUserId = portfolioId, iActionID = actionId }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Split Deleted");
    }

    public async Task<IReadOnlyList<CapitalReturnRecord>> GetCapitalReturnsAsync(int portfolioId)
    {
        await using var con = db.Create();
        // The tax reports call this too, so they keep working until the migration adds the table
        var rows = await con.QueryAsync<CapitalReturnRecord>("""
            IF OBJECT_ID('dbo.tblReturnsOfCapital', 'U') IS NOT NULL
                SELECT iReturnOfCapitalID AS ReturnId, strStockExchangeCode AS ExchangeCode, strStockCode AS StockCode,
                       CAST(dtmPaymentDate AS datetime) AS Date, dblAmountPerUnit AS PerUnit, dblTotalAmount AS Total, strComment AS Comment
                FROM tblReturnsOfCapital
                WHERE iUserID = @portfolioId
                ORDER BY dtmPaymentDate DESC, iReturnOfCapitalID DESC
            ELSE
                SELECT CAST(0 AS int) AS ReturnId, '' AS ExchangeCode, '' AS StockCode, CAST(NULL AS datetime) AS Date,
                       CAST(NULL AS float) AS PerUnit, CAST(0 AS decimal(19, 4)) AS Total, CAST(NULL AS varchar(500)) AS Comment
                WHERE 1 = 0
            """, new { portfolioId });
        return rows.ToList();
    }

    /// <summary>The portfolio's returns of capital, demergers and takeovers for the CGT parcel matching.</summary>
    public async Task<IReadOnlyList<CapitalReturn>> GetCapitalReturnLotsAsync(int portfolioId) =>
        [.. (await GetCapitalReturnsAsync(portfolioId))
            .Select(r => new CapitalReturn(r.ExchangeCode, r.StockCode, DateOnly.FromDateTime(r.Date), r.Total)),
         .. (await GetParcelTransfersAsync(portfolioId)).Select(t => t.ToLot()).OfType<CapitalReturn>()];

    /// <summary>Demergers, takeovers and code changes (tblParcelTransfers), oldest first; none until the migration adds the table.</summary>
    public async Task<IReadOnlyList<ParcelTransferRecord>> GetParcelTransfersAsync(int portfolioId)
    {
        await using var con = db.Create();
        var exists = await con.ExecuteScalarAsync<int>("SELECT CASE WHEN OBJECT_ID('dbo.uspGetParcelTransfers', 'P') IS NULL THEN 0 ELSE 1 END");
        if (exists == 0)
            return [];
        var rows = await con.QueryAsync<ParcelTransferRecord>("uspGetParcelTransfers", new { iUserID = portfolioId }, commandType: CommandType.StoredProcedure);
        return rows.ToList();
    }

    /// <summary>
    /// For tax: the returns of capital plus each fund tax statement's cost base change and capital gains at its year end.
    /// The ledger uses <see cref="GetCapitalReturnLotsAsync"/> only, as statements post no journals.
    /// </summary>
    public async Task<IReadOnlyList<CapitalReturn>> GetTaxCostBaseLotsAsync(int portfolioId) =>
        [.. await GetCapitalReturnLotsAsync(portfolioId),
         .. (await new FundTaxStatementService(db).GetAsync(portfolioId)).Select(s => s.ToCostBaseLot())];

    /// <summary>Records a return of capital with uspAddReturnOfCapital, which posts its journal for double-entry portfolios.</summary>
    public async Task<ProcResult> AddCapitalReturnAsync(int portfolioId, NewCapitalReturn r, decimal total)
    {
        await using var con = db.Create();
        var row = (IDictionary<string, object?>)await con.QuerySingleAsync("uspAddReturnOfCapital", new
        {
            iUserId = portfolioId,
            strStockExchangeCode = r.Market,
            strStockCode = r.Code,
            dtmPaymentDate = r.Date.ToDateTime(TimeOnly.MinValue),
            dblTotalAmount = total,
            dblAmountPerUnit = (double?)r.PerUnit,
            strComment = r.Comment,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(Text(row, "Result"), "Return Of Capital Added", Int(row, "iReturnOfCapitalID"));
    }

    /// <summary>Deletes a return of capital and its journal. Null when it isn't one of this portfolio's.</summary>
    public async Task<ProcResult?> DeleteCapitalReturnAsync(int portfolioId, int returnId)
    {
        await using var con = db.Create();
        var owned = await con.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tblReturnsOfCapital WHERE iReturnOfCapitalID = @returnId AND iUserID = @portfolioId",
            new { portfolioId, returnId });
        if (owned == 0)
            return null;

        var result = await con.ExecuteScalarAsync<string>("uspDeleteReturnOfCapital",
            new { iUserId = portfolioId, iReturnOfCapitalID = returnId }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Return Of Capital Deleted");
    }

    private static string? Text(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var v) && v is not null and not DBNull ? Convert.ToString(v) : null;

    private static int? Int(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var v) && v is not null and not DBNull ? Convert.ToInt32(v) : null;
}
