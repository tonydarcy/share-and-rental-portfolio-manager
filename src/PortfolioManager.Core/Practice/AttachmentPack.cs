using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PortfolioManager.Core.Practice;

/// <summary>One file in the printed supporting documents, and the page it starts on (null when it couldn't be printed).</summary>
public sealed record PackDocument(string FileName, string For, string? Note, int? StartPage, string? Reason);

public sealed record AttachmentPackResult(byte[] Pdf, IReadOnlyList<PackDocument> Documents)
{
    public int Printed => Documents.Count(d => d.StartPage is not null);
}

/// <summary>
/// Prints a year's attachments into one PDF to go behind a report pack or SMSF audit pack: a contents page, then each
/// PDF's own pages, each picture on a page of its own and each text or CSV file as plain text. Word, Excel and email
/// files can't be turned into pages without Office, so the contents page lists them to send separately.
/// </summary>
public static class AttachmentPack
{
    private const double Width = 595, Height = 842, Margin = 50; // A4 in points
    private const int LinesPerPage = 60, CharsPerLine = 95;

    public static bool CanPrint(string contentType) =>
        contentType is "application/pdf" or "image/jpeg" or "image/png" or "text/plain" or "text/csv";

    /// <summary>Why a file can't go into the pack, or null when it can.</summary>
    public static string? NotPrintable(string contentType) => CanPrint(contentType) ? null : contentType switch
    {
        "image/gif" or "image/webp" or "image/heic" => "This picture type can't be printed into a PDF; save it as JPEG or PNG and upload it again.",
        _ => "Word, Excel and email files can't be printed into the pack; send them with it, or save them as PDF and upload that.",
    };

    public static AttachmentPackResult Build(string title, IReadOnlyList<(Attachment Info, AttachmentFile File)> files)
    {
        var parts = new List<byte[]>();
        var docs = new List<PackDocument>();
        var page = 0;
        var ordered = files.OrderBy(f => f.Info.Kind).ThenBy(f => f.Info.FileName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var contentsPages = (int)Math.Ceiling(Math.Max(1, ordered.Count + 4) / (double)(LinesPerPage / 2));
        page = contentsPages;
        foreach (var (info, file) in ordered)
        {
            var label = AttachmentKinds.Label(info.Kind) + (info.TargetId is { } t ? $" {t}" : "");
            byte[]? part = null;
            string? reason = NotPrintable(info.ContentType);
            if (reason is null)
            {
                try
                {
                    part = info.ContentType switch
                    {
                        "application/pdf" => CheckPdf(file.Data),
                        "image/jpeg" or "image/png" => ImagePage(info.FileName, file.Data, info.ContentType == "image/png"),
                        _ => TextPages(info.FileName, Encoding.UTF8.GetString(file.Data)),
                    };
                }
                catch (Exception)
                {
                    reason = "The file couldn't be read (it may be password protected or damaged); send it separately.";
                }
            }
            if (part is null)
            {
                docs.Add(new PackDocument(info.FileName, label, info.Note, null, reason));
                continue;
            }
            docs.Add(new PackDocument(info.FileName, label, info.Note, page + 1, null));
            page += PageCount(part);
            parts.Add(part);
        }
        var contents = ContentsPages(title, docs, contentsPages);
        var pdf = parts.Count == 0 ? contents : PdfMerger.Merge([contents, .. parts]);
        return new AttachmentPackResult(pdf, docs);
    }

    public static int PageCount(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.NumberOfPages;
    }

    /// <summary>Opens the PDF to make sure it can be read before it is merged.</summary>
    private static byte[] CheckPdf(byte[] data)
    {
        using var doc = PdfDocument.Open(data);
        if (doc.NumberOfPages == 0)
            throw new InvalidOperationException("No pages.");
        return data;
    }

    private static byte[] ContentsPages(string title, IReadOnlyList<PackDocument> docs, int pageCount)
    {
        var lines = new List<(string Text, bool Bold)>
        {
            (title, true),
            ("Supporting documents", true),
            ("", false),
        };
        if (docs.Count == 0)
            lines.Add(("No files are attached to this year.", false));
        foreach (var d in docs)
        {
            var start = d.StartPage is { } p ? $"page {p}" : "not printed";
            lines.Add((Fit($"{d.FileName} ({d.For})", CharsPerLine - 14) + "  " + start, false));
            if (d.Reason is not null)
                lines.Add(("    " + Fit(d.Reason, CharsPerLine - 4), false));
            else if (!string.IsNullOrWhiteSpace(d.Note))
                lines.Add(("    " + Fit(d.Note!, CharsPerLine - 4), false));
        }
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var perPage = (int)Math.Ceiling(lines.Count / (double)pageCount);
        for (var i = 0; i < pageCount; i++)
        {
            var pageBuilder = builder.AddPage(Width, Height);
            var y = Height - Margin;
            foreach (var (text, isBold) in lines.Skip(i * perPage).Take(perPage))
            {
                if (text.Length > 0)
                    pageBuilder.AddText(Clean(text), isBold ? 12 : 9, new PdfPoint(Margin, y), isBold ? bold : font);
                y -= isBold ? 18 : 12.5;
            }
        }
        return builder.Build();
    }

    private static byte[] ImagePage(string fileName, byte[] data, bool png)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(Width, Height);
        page.AddText(Clean(Fit(fileName, CharsPerLine)), 9, new PdfPoint(Margin, Height - Margin + 15), font);
        var box = new PdfRectangle(Margin, Margin, Width - Margin, Height - Margin);
        if (png)
            page.AddPng(data, box);
        else
            page.AddJpeg(data, box);
        return builder.Build();
    }

    private static byte[] TextPages(string fileName, string text)
    {
        var lines = Wrap(text).ToList();
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Courier);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var pages = Math.Max(1, (int)Math.Ceiling(lines.Count / (double)LinesPerPage));
        for (var i = 0; i < pages; i++)
        {
            var page = builder.AddPage(Width, Height);
            page.AddText(Clean(Fit(fileName, CharsPerLine)) + (pages > 1 ? $" ({i + 1} of {pages})" : ""), 10, new PdfPoint(Margin, Height - Margin + 15), bold);
            var y = Height - Margin - 5;
            foreach (var line in lines.Skip(i * LinesPerPage).Take(LinesPerPage))
            {
                if (line.Length > 0)
                    page.AddText(Clean(line), 8, new PdfPoint(Margin, y), font);
                y -= 12;
            }
        }
        return builder.Build();
    }

    /// <summary>Splits text into lines short enough for the page, breaking long lines rather than losing them.</summary>
    public static IEnumerable<string> Wrap(string text, int width = CharsPerLine)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Replace("\t", "    ");
            if (line.Length == 0)
                yield return "";
            for (var i = 0; i < line.Length; i += width)
                yield return line.Substring(i, Math.Min(width, line.Length - i));
        }
    }

    private static string Fit(string text, int width) => text.Length <= width ? text : text[..(width - 3)] + "...";

    /// <summary>The standard PDF fonts only have Western European letters, so anything else becomes a question mark.</summary>
    public static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(c is >= ' ' and <= '~' || c is >= ' ' and <= 'ÿ' ? c : c is '‘' or '’' ? '\'' : c is '“' or '”' ? '"' : c is '–' or '—' ? '-' : '?');
        return sb.ToString();
    }
}
