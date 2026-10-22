using System.Text;
using PortfolioManager.Core.Imports;

namespace PortfolioManager.Web.Services;

/// <summary>
/// Reads an uploaded CSV, and carries its text from the preview to the import as base64
/// (so line breaks survive the round trip). The import re-parses and re-checks it.
/// </summary>
public static class CsvUpload
{
    // Room for a 2 MB file as base64 (under the 4 MB form value limit) plus the rest of the form
    public const long RequestLimit = 6 * 1024 * 1024;
    private const int MaxEncodedLength = 4 * 1024 * 1024;

    public static async Task<(string? Text, string? Error)> ReadAsync(IFormFile? file)
    {
        var error = CsvImport.CheckFile(file?.Length ?? 0, file?.FileName);
        if (error is not null)
            return (null, error);

        using var reader = new StreamReader(file!.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return (await reader.ReadToEndAsync(), null);
    }

    public static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    public static string? Decode(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxEncodedLength)
            return null;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
