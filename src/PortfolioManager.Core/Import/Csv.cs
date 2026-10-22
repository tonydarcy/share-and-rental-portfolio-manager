using System.Text;

namespace PortfolioManager.Core.Import;

/// <summary>A small RFC 4180 CSV reader: quoted fields, doubled quotes, commas and line breaks inside quotes.</summary>
public static class Csv
{
    /// <summary>Splits the text into rows of fields. Blank lines are dropped; each row keeps its 1-based line number.</summary>
    public static IReadOnlyList<(int Line, string[] Fields)> Parse(string text)
    {
        var rows = new List<(int, string[])>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var rowStart = 1;

        // Excel and most exporters write a byte-order mark
        var i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;

        void EndRow()
        {
            fields.Add(field.ToString());
            field.Clear();
            if (fields.Any(f => f.Trim().Length > 0))
                rows.Add((rowStart, fields.ToArray()));
            fields.Clear();
        }

        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                        inQuotes = false;
                }
                else
                {
                    if (c == '\n')
                        line++;
                    field.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"' when field.ToString().Trim().Length == 0:
                    field.Clear();
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    EndRow();
                    line++;
                    rowStart = line;
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
            EndRow();
        return rows;
    }
}
