using System.Text;
using PortfolioManager.Core.Practice;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PortfolioManager.Tests;

public class AttachmentPackTests
{
    private static (Attachment, AttachmentFile) File(int id, string name, string type, byte[] data) =>
        (new Attachment(id, AttachmentKinds.Year, null, 2026, name, type, data.Length, null, null, DateTime.Today), new AttachmentFile(name, type, data));

    private static byte[] Pdf(int pages)
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 0; i < pages; i++)
            b.AddPage(PageSize.A4).AddText($"Page {i + 1}", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        return b.Build();
    }

    [Fact]
    public void Files_are_printed_after_a_contents_page_with_their_page_numbers()
    {
        var files = new[]
        {
            File(1, "bank statement.pdf", "application/pdf", Pdf(3)),
            File(2, "notes.txt", "text/plain", Encoding.UTF8.GetBytes("Line one\nLine two")),
            File(3, "minutes.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", [1, 2, 3]),
        };
        var pack = AttachmentPack.Build("Smith Super Fund: 2025-26", files);
        Assert.Equal(2, pack.Printed);
        var docs = pack.Documents.ToDictionary(d => d.FileName);
        Assert.Equal(2, docs["bank statement.pdf"].StartPage);
        Assert.Equal(5, docs["notes.txt"].StartPage);
        Assert.Null(docs["minutes.docx"].StartPage);
        Assert.NotNull(docs["minutes.docx"].Reason);
        Assert.Equal(5, AttachmentPack.PageCount(pack.Pdf));
    }

    [Fact]
    public void A_damaged_pdf_is_listed_rather_than_stopping_the_pack()
    {
        var pack = AttachmentPack.Build("Pack", [File(1, "broken.pdf", "application/pdf", Encoding.ASCII.GetBytes("not a pdf"))]);
        var doc = Assert.Single(pack.Documents);
        Assert.Null(doc.StartPage);
        Assert.Contains("couldn't be read", doc.Reason);
        Assert.Equal(1, AttachmentPack.PageCount(pack.Pdf));
    }

    [Fact]
    public void Long_lines_wrap_and_odd_characters_are_replaced()
    {
        var lines = AttachmentPack.Wrap(new string('x', 200), 95).ToList();
        Assert.Equal([95, 95, 10], lines.Select(l => l.Length));
        Assert.Equal("'quoted' - ? ok", AttachmentPack.Clean("‘quoted’ – 中 ok"));
        Assert.Null(AttachmentPack.NotPrintable("image/png"));
        Assert.NotNull(AttachmentPack.NotPrintable("image/heic"));
    }
}
