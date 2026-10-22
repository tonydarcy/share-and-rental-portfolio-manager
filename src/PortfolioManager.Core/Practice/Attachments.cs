using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Practice;

public static class AttachmentKinds
{
    public const string Trade = "Trade", Journal = "Journal", Client = "Client", Year = "Year", Workpaper = "Workpaper", Query = "Query",
        Bill = "Bill", ExpenseClaim = "ExpenseClaim";
    public static readonly IReadOnlyList<string> All = [Trade, Journal, Client, Year, Workpaper, Query, Bill, ExpenseClaim];

    public static string Label(string kind) => kind switch
    {
        Trade => "Trade",
        Journal => "Journal",
        Client => "Client (permanent file)",
        Year => "Year",
        Workpaper => "Workpaper",
        Bill => "Bill",
        ExpenseClaim => "Expense claim",
        _ => "Client query",
    };
}

/// <summary>A file kept against a record. The content is only read when it is downloaded.</summary>
public sealed record Attachment(int Id, string Kind, int? TargetId, int? Year, string FileName, string ContentType, int Size, string? Note,
    string? UploadedBy, DateTime Uploaded)
{
    public string SizeText => Size >= 1_048_576 ? $"{Size / 1_048_576m:0.0} MB" : $"{Math.Max(1, Size / 1024)} KB";
}

public sealed record AttachmentFile(string FileName, string ContentType, byte[] Data);

public static class AttachmentRules
{
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>
    /// The file types that can be uploaded, with the content type they are served as. The browser's own content type
    /// is never trusted, so an uploaded page can't run as part of the site.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".heic"] = "image/heic",
        [".csv"] = "text/csv",
        [".txt"] = "text/plain",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".xls"] = "application/vnd.ms-excel",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".doc"] = "application/msword",
        [".msg"] = "application/vnd.ms-outlook",
        [".eml"] = "message/rfc822",
    };

    public static string? ContentTypeFor(string fileName) =>
        Types.TryGetValue(Path.GetExtension(fileName ?? ""), out var type) ? type : null;

    public static string? Validate(string? fileName, long size)
    {
        if (string.IsNullOrWhiteSpace(fileName) || size <= 0)
            return "Choose a file to upload.";
        if (size > MaxBytes)
            return "That file is bigger than 10 MB.";
        if (ContentTypeFor(fileName) is null)
            return "That type of file can't be uploaded. Use PDF, a picture, Word, Excel, CSV, text or an email.";
        return null;
    }

    /// <summary>The name without any folder, at most 260 characters.</summary>
    public static string CleanName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/')).Trim();
        return name.Length > 260 ? name[^260..] : name;
    }
}

/// <summary>tblAttachments (2026-10-21-15-attachments-audit.sql).</summary>
public sealed class AttachmentService(IDbConnectionFactory db)
{
    private sealed record Row(int Id, string Kind, int? TargetId, int? Year, string FileName, string ContentType, int Size, string? Note,
        string? UploadedBy, DateTime Uploaded);

    /// <summary>
    /// The portfolio's files, optionally only those of one kind and record, or one year (a year's files include those
    /// on its trades, journals and workpapers). Empty until the migration has been run.
    /// </summary>
    public async Task<IReadOnlyList<Attachment>> ListAsync(int portfolioId, string? kind = null, int? targetId = null, int? year = null)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync<Row>("""
                SELECT A.iAttachmentID AS Id, A.strKind AS Kind, A.iTargetID AS TargetId, A.iYear AS Year, A.strFileName AS FileName,
                       A.strContentType AS ContentType, A.iSize AS Size, A.strNote AS Note, U.strLoginEmail AS UploadedBy, A.dtmUploaded AS Uploaded
                FROM tblAttachments A LEFT JOIN tblUsers U ON U.iUserId = A.iUploadedByUserID
                WHERE A.iUserID = @portfolioId
                  AND (@kind IS NULL OR A.strKind = @kind)
                  AND (@targetId IS NULL OR A.iTargetID = @targetId)
                  AND (@year IS NULL OR A.iYear = @year)
                ORDER BY A.dtmUploaded DESC
                """, new { portfolioId, kind, targetId, year });
            return rows.Select(r => new Attachment(r.Id, r.Kind, r.TargetId, r.Year, r.FileName, r.ContentType, r.Size, r.Note, r.UploadedBy, r.Uploaded)).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    /// <summary>How many files each record has, for badges on lists.</summary>
    public async Task<IReadOnlyDictionary<int, int>> CountsAsync(int portfolioId, string kind) =>
        (await ListAsync(portfolioId, kind)).Where(a => a.TargetId is not null).GroupBy(a => a.TargetId!.Value).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>The file, only if it belongs to the portfolio.</summary>
    public async Task<AttachmentFile?> GetFileAsync(int portfolioId, int id)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync<(string FileName, string ContentType, byte[] Data)?>(
            "SELECT strFileName, strContentType, binData FROM tblAttachments WHERE iAttachmentID = @id AND iUserID = @portfolioId",
            new { id, portfolioId });
        return row is { } r ? new AttachmentFile(r.FileName, r.ContentType, r.Data) : null;
    }

    public async Task<ProcResult> AddAsync(int portfolioId, int loginUserId, string kind, int? targetId, int? year, string fileName, byte[] data, string? note)
    {
        if (!AttachmentKinds.All.Contains(kind))
            return ProcResult.Failed("Choose what the file is for.");
        if (AttachmentRules.Validate(fileName, data.Length) is { } error)
            return ProcResult.Failed(error);
        var name = AttachmentRules.CleanName(fileName);
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspAddAttachment", new
        {
            iUserID = portfolioId, strKind = kind, iTargetID = targetId, iYear = year, strFileName = name,
            strContentType = AttachmentRules.ContentTypeFor(name), binData = data, strNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            iUploadedByUserID = loginUserId,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> DeleteAsync(int portfolioId, int id)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteAttachment", new { iUserID = portfolioId, iAttachmentID = id },
            commandType: CommandType.StoredProcedure), "Success");
    }
}
