using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Practice;

public static class QueryStatuses
{
    public const string Open = "Open", Answered = "Answered", Closed = "Closed";

    public static string Label(string s) => s switch
    {
        Open => "Waiting for the client",
        Answered => "Answered",
        _ => "Closed",
    };
}

/// <summary>A "what is this transaction?" question to the client, and their answer (tblClientQueries).</summary>
public sealed record ClientQuery(int Id, int? JournalId, int? JournalLineId, DateTime? TransactionDate, decimal? Amount, string? Transaction,
    string Question, string? AskedBy, DateTime Asked, string? Answer, string? AnsweredBy, DateTime? Answered, string Status)
{
    public bool IsOpen => Status == QueryStatuses.Open;
}

public sealed class ClientQueryService(IDbConnectionFactory db)
{
    /// <summary>Empty until the migration has been run.</summary>
    public async Task<IReadOnlyList<ClientQuery>> ListAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            return (await con.QueryAsync<ClientQuery>("""
                SELECT Q.iQueryID AS Id, Q.iJournalID AS JournalId, Q.iJournalLineID AS JournalLineId, CAST(Q.dtmTransaction AS datetime) AS TransactionDate,
                       Q.dblAmount AS Amount, Q.strTransaction AS [Transaction], Q.strQuestion AS Question, A.strLoginEmail AS AskedBy, Q.dtmAsked AS Asked,
                       Q.strAnswer AS Answer, B.strLoginEmail AS AnsweredBy, Q.dtmAnswered AS Answered, Q.strStatus AS Status
                FROM tblClientQueries Q
                LEFT JOIN tblUsers A ON A.iUserId = Q.iAskedByUserID
                LEFT JOIN tblUsers B ON B.iUserId = Q.iAnsweredByUserID
                WHERE Q.iUserID = @portfolioId
                ORDER BY CASE Q.strStatus WHEN 'Open' THEN 0 WHEN 'Answered' THEN 1 ELSE 2 END, Q.dtmAsked DESC
                """, new { portfolioId })).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    public async Task<ProcResult> AskAsync(int portfolioId, int loginUserId, string question, int? journalId, int? lineId, DateOnly? date, decimal? amount, string? transaction)
    {
        if (string.IsNullOrWhiteSpace(question))
            return ProcResult.Failed("Enter the question.");
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspAddClientQuery", new
        {
            iUserID = portfolioId, iJournalID = journalId, iJournalLineID = lineId, dtmTransaction = date?.ToDateTime(TimeOnly.MinValue),
            dblAmount = amount, strTransaction = Limit(transaction, 500), strQuestion = Limit(question, 2000), iAskedByUserID = loginUserId,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> AnswerAsync(int portfolioId, int queryId, string answer, int loginUserId) =>
        await ResultAsync("uspAnswerClientQuery", new { iUserID = portfolioId, iQueryID = queryId, strAnswer = Limit(answer, 2000), iAnsweredByUserID = loginUserId });

    public async Task<ProcResult> SetStatusAsync(int portfolioId, int queryId, bool closed) =>
        await ResultAsync("uspSetClientQueryStatus", new { iUserID = portfolioId, iQueryID = queryId, strStatus = closed ? QueryStatuses.Closed : QueryStatuses.Open });

    public async Task<ProcResult> DeleteAsync(int portfolioId, int queryId) =>
        await ResultAsync("uspDeleteClientQuery", new { iUserID = portfolioId, iQueryID = queryId });

    private async Task<ProcResult> ResultAsync(string proc, object args)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>(proc, args, commandType: CommandType.StoredProcedure), "Success");
    }

    private static string? Limit(string? s, int max) => s?.Trim() is { Length: > 0 } t ? (t.Length > max ? t[..max] : t) : null;
}
