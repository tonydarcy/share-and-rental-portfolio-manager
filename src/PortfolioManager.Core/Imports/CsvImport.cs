using System.Globalization;
using System.Text;

namespace PortfolioManager.Core.Imports;

/// <summary>One line of an uploaded file: the parsed value when it is valid, otherwise the reasons it isn't.</summary>
public sealed record ParsedLine<T>(int LineNumber, string Text, T? Value, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
    where T : class
{
    public bool IsValid => Value is not null && Errors.Count == 0;
}

public sealed record ImportPreview<T>(IReadOnlyList<ParsedLine<T>> Lines, string? FileError = null)
    where T : class
{
    public int ValidCount => Lines.Count(l => l.IsValid);
    public int ErrorCount => Lines.Count(l => !l.IsValid);
    public IEnumerable<T> ValidRows => Lines.Where(l => l.IsValid).Select(l => l.Value!);
    public static ImportPreview<T> Failed(string error) => new([], error);
}

public static class CsvImport
{
    // Uploads are posted back with the preview, so they must stay well under the 4 MB form value limit
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxLines = 20000;

    /// <summary>Splits text into non-blank lines with their 1-based line numbers, dropping a UTF-8 byte order mark.</summary>
    public static IEnumerable<(int Number, string Text)> Lines(string text)
    {
        var lines = text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
                yield return (i + 1, lines[i].Trim());
        }
    }

    /// <summary>Splits one CSV line into fields. Fields may be wrapped in double quotes ("" for a quote inside).</summary>
    public static IReadOnlyList<string> Split(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                    quoted = false;
                else
                    field.Append(c);
            }
            else if (c == '"')
                quoted = true;
            else if (c == ',')
            {
                fields.Add(field.ToString().Trim());
                field.Clear();
            }
            else
                field.Append(c);
        }
        fields.Add(field.ToString().Trim());
        return fields;
    }

    /// <summary>Common checks on the raw upload before any line is parsed.</summary>
    public static string? CheckFile(long length, string? fileName)
    {
        if (length <= 0)
            return "Choose a CSV file to upload.";
        if (length > MaxFileBytes)
            return $"The file is too large. The limit is {MaxFileBytes / (1024 * 1024)} MB; split it into smaller files.";
        var ext = Path.GetExtension(fileName ?? "");
        if (!ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return "The file must be a .csv (or .txt) file.";
        return null;
    }

    internal static bool TryNumber(string s, out decimal value) =>
        decimal.TryParse(s.Replace("$", ""), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
}
