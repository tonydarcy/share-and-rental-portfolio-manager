using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Accounting;

/// <summary>What switching a portfolio to double entry would post (uspGetDoubleEntryConversion).</summary>
public sealed record DoubleEntryConversionPreview(
    string Mode,
    bool CanSwitch,
    int Trades,
    int Cash,
    int Payouts,
    int CorporateActions,
    int Other,
    DateOnly? Earliest,
    DateOnly? LockedTo,
    int BankImportJournals,
    int ManualJournals)
{
    public bool PostsJournals => AccountingMode.PostsJournals(Mode);

    /// <summary>Every record that would get a journal.</summary>
    public int Records => Trades + Cash + Payouts + CorporateActions + Other;

    /// <summary>Some records to post are dated on or before the lock, so the switch would be refused.</summary>
    public bool Locked => LockedTo is { } to && Earliest is { } first && first <= to;

    /// <summary>
    /// Imported bank lines are already in the ledger. If any were allocated to income, expense or capital accounts,
    /// the payouts and cash now posted record the same money again.
    /// </summary>
    public bool MayDoubleUp => BankImportJournals > 0 && (Payouts > 0 || Cash > 0);
}

/// <summary>
/// Switches an old single-entry portfolio to double entry. uspConvertToDoubleEntry posts the journal each trade,
/// settlement, cash entry, payout, DRP, return of capital, demerger, fund statement and loan entry would have posted
/// had the portfolio been double entry from the start, and changes the mode, in one transaction. Then each sale's
/// FIFO gain is moved to Realised Gains, as the ledger pages do before they read it.
/// </summary>
public sealed class DoubleEntryConversionService(IDbConnectionFactory db, RealisedGainService gains)
{
    // A portfolio with years of records posts thousands of journals, so allow longer than the default 30 seconds
    private const int LongTimeout = 300;

    public static object Parameters(int userId, int portfolioId) => new { iUserId = userId, iPortfolioId = portfolioId };

    public async Task<DoubleEntryConversionPreview?> GetPreviewAsync(int userId, int portfolioId)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync("uspGetDoubleEntryConversion", Parameters(userId, portfolioId),
            commandType: CommandType.StoredProcedure, commandTimeout: LongTimeout);
        return row is null ? null : MapPreview((IDictionary<string, object?>)row);
    }

    public static DoubleEntryConversionPreview MapPreview(IDictionary<string, object?> r) => new(
        Db.Str(r, "strAccountingMode") is { Length: > 0 } mode ? mode : "SingleEntry",
        Db.Bool(r, "bCanSwitch") ?? false,
        Db.Int(r, "iTrades") ?? 0,
        Db.Int(r, "iCash") ?? 0,
        Db.Int(r, "iPayouts") ?? 0,
        Db.Int(r, "iCorporateActions") ?? 0,
        Db.Int(r, "iOther") ?? 0,
        Db.Date(r, "dtmEarliest"),
        Db.Date(r, "dtmLockedTo"),
        Db.Int(r, "iBankImportJournals") ?? 0,
        Db.Int(r, "iManualJournals") ?? 0);

    private sealed record ConvertRow(string? Result, int iJournals);

    /// <summary>Switches the portfolio. On success, Id is the number of journals posted.</summary>
    public async Task<ProcResult> ConvertAsync(int userId, int portfolioId)
    {
        ConvertRow? row;
        await using (var con = db.Create())
        {
            row = await con.QuerySingleOrDefaultAsync<ConvertRow>("uspConvertToDoubleEntry", Parameters(userId, portfolioId),
                commandType: CommandType.StoredProcedure, commandTimeout: LongTimeout);
        }
        var result = ProcResult.From(row?.Result, "Switched", row?.iJournals);
        if (result.Ok)
            await gains.SyncAsync(portfolioId);
        return result;
    }
}
