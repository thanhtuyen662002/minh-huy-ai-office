using System.IO.Compression;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class KnowledgeFileTextTests
{
    private const string Word = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
    private const string Sheet = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
    private const string Rels = "xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"";

    private static byte[] Zip(params (string Path, string Xml)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, xml) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }
        return buffer.ToArray();
    }

    [Fact]
    public void ReadsWordHeadingsListsAndTables()
    {
        var styles = $"<w:styles {Word}><w:style w:styleId=\"u1\"><w:name w:val=\"heading 1\"/></w:style><w:style w:styleId=\"u2\"><w:name w:val=\"heading 2\"/></w:style></w:styles>";
        var body = $"""
            <w:document {Word}><w:body>
            <w:p><w:pPr><w:pStyle w:val="u1"/></w:pPr><w:r><w:t>Khoá sổ</w:t></w:r></w:p>
            <w:p><w:pPr><w:pStyle w:val="u2"/></w:pPr><w:r><w:t>Bước 1</w:t></w:r></w:p>
            <w:p><w:r><w:t xml:space="preserve">Kiểm tra </w:t></w:r><w:r><w:t>phiếu</w:t><w:tab/><w:t>thu</w:t></w:r></w:p>
            <w:p><w:pPr><w:numPr><w:ilvl w:val="0"/></w:numPr></w:pPr><w:r><w:t>Đối chiếu</w:t></w:r><w:r><w:delText>bỏ</w:delText></w:r></w:p>
            <w:p/>
            <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Mã</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Tên</w:t></w:r></w:p><w:p><w:r><w:t>hàng</w:t></w:r></w:p></w:tc></w:tr>
            <w:tr><w:tc><w:p><w:r><w:t>SP01</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Thuốc A</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            <w:p><w:r><w:t>Hết</w:t></w:r></w:p>
            </w:body></w:document>
            """;
        var extract = KnowledgeFileText.Extract(@"C:\tài liệu\Hướng dẫn.docx", Zip(("word/styles.xml", styles), ("word/document.xml", body)));
        Assert.Equal("Hướng dẫn", extract.Title);
        Assert.False(extract.Truncated);
        Assert.Equal("# Khoá sổ\n## Bước 1\nKiểm tra phiếu\tthu\n- Đối chiếu\n\nMã | Tên hàng\nSP01 | Thuốc A\n\nHết", Assert.Single(extract.Parts));
    }

    [Fact]
    public void ReadsEverySheetWithSharedStringsAndDates()
    {
        var bytes = Zip(
            ("xl/workbook.xml", $"<workbook {Sheet}><sheets><sheet name=\"Tồn kho\" sheetId=\"1\" r:id=\"rId2\"/><sheet name=\"Khách\" sheetId=\"2\" r:id=\"rId1\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", $"<Relationships {Rels}><Relationship Id=\"rId1\" Target=\"worksheets/b.xml\"/><Relationship Id=\"rId2\" Target=\"/xl/worksheets/a.xml\"/></Relationships>"),
            ("xl/sharedStrings.xml", $"<sst {Sheet}><si><t>Mã</t></si><si><r><t>Ngày </t></r><r><t>nhập</t></r><rPh><t>x</t></rPh></si></sst>"),
            ("xl/styles.xml", $"<styleSheet {Sheet}><numFmts><numFmt numFmtId=\"164\" formatCode=\"dd/mm/yyyy\"/></numFmts><cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"164\"/></cellXfs></styleSheet>"),
            ("xl/worksheets/a.xml", $"<worksheet {Sheet}><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"C1\" t=\"s\"><v>1</v></c></row>"
                + "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>SP01</t></is></c><c r=\"B2\"><f>1+1</f><v>2</v></c><c r=\"C2\" s=\"1\"><v>46296</v></c></row><row r=\"3\"/></sheetData></worksheet>"),
            ("xl/worksheets/b.xml", $"<worksheet {Sheet}><sheetData><row r=\"1\"><c r=\"A1\" t=\"b\"><v>1</v></c></row></sheetData></worksheet>"));
        var text = Assert.Single(KnowledgeFileText.Extract("ton-kho.xlsx", bytes).Parts);
        Assert.Equal("# Tồn kho\nMã |  | Ngày nhập\nSP01 | 2 | 01/10/2026\n\n# Khách\nTRUE", text);
    }

    [Fact]
    public void ReadsSlidesInPresentationOrder()
    {
        const string presentation = "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        const string drawing = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
        var bytes = Zip(
            ("ppt/presentation.xml", $"<p:presentation {presentation}><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId9\"/><p:sldId id=\"257\" r:id=\"rId8\"/></p:sldIdLst></p:presentation>"),
            ("ppt/_rels/presentation.xml.rels", $"<Relationships {Rels}><Relationship Id=\"rId8\" Target=\"slides/slide1.xml\"/><Relationship Id=\"rId9\" Target=\"slides/slide2.xml\"/></Relationships>"),
            ("ppt/slides/slide1.xml", $"<sld {drawing}><a:p><a:r><a:t>Sau</a:t></a:r></a:p></sld>"),
            ("ppt/slides/slide2.xml", $"<sld {drawing}><a:p><a:r><a:t>Giới </a:t></a:r><a:r><a:t>thiệu</a:t></a:r></a:p><a:p><a:r><a:t>Dòng hai</a:t></a:r></a:p></sld>"));
        Assert.Equal("# Trang chiếu 1\nGiới thiệu\nDòng hai\n\n# Trang chiếu 2\nSau", Assert.Single(KnowledgeFileText.Extract("a.pptx", bytes).Parts));
    }

    [Fact]
    public void ReadsPdfPagesAndRejectsPdfsWithoutText()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("Support process", 12, new PdfPoint(72, 750), font);
        builder.AddPage(PageSize.A4).AddText("Second page", 12, new PdfPoint(72, 750), font);
        var text = Assert.Single(KnowledgeFileText.Extract("guide.pdf", builder.Build()).Parts);
        Assert.Equal("Support process\n\nSecond page", text);
        var blank = new PdfDocumentBuilder();
        blank.AddPage(PageSize.A4);
        Assert.Equal("empty", Assert.Throws<KnowledgeFileUnreadableException>(() => KnowledgeFileText.Extract("scan.pdf", blank.Build())).Reason);
    }

    [Theory]
    [InlineData("old.doc", "legacy")]
    [InlineData("old.XLS", "legacy")]
    [InlineData("photo.png", "unsupported")]
    [InlineData("noextension", "unsupported")]
    [InlineData("fake.docx", "damaged")]
    [InlineData("fake.pdf", "damaged")]
    public void ExplainsFilesItCannotRead(string name, string reason) =>
        Assert.Equal(reason, Assert.Throws<KnowledgeFileUnreadableException>(() => KnowledgeFileText.Extract(name, "not a document"u8.ToArray())).Reason);

    [Fact]
    public void DecodesTextFilesAndCleansControlCharacters()
    {
        Assert.Equal("Ghi chú\n\nDòng 2", Assert.Single(KnowledgeFileText.Extract("a.txt", [.. "\uFEFFGhi chú\r\n\r\n\r\n\u0001Dòng 2  \n\n"u8]).Parts));
        Assert.Equal("Đà Nẵng", Assert.Single(KnowledgeFileText.Extract("b.md", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Đà Nẵng")).ToArray()).Parts));
        var legacy = Encoding.GetEncoding(1258).GetBytes("Ghi chú");
        Assert.Equal("Ghi chú", Assert.Single(KnowledgeFileText.Extract("c.csv", legacy).Parts));
        Assert.Equal("empty", Assert.Throws<KnowledgeFileUnreadableException>(() => KnowledgeFileText.Extract("d.txt", "  \n\t\n"u8.ToArray())).Reason);
        Assert.Equal("empty", Assert.Throws<KnowledgeFileUnreadableException>(() => KnowledgeFileText.Extract("e.txt", [])).Reason);
    }

    [Fact]
    public void SplitsLongTextAtLineBreaksAndStopsAtThePartLimit()
    {
        var line = "Dòng nội dung tiếng Việt khá dài để kiểm tra việc chia trang.";
        var lines = Enumerable.Range(0, KnowledgePageJson.MaximumBody * 2 / line.Length).Select(index => $"{index} {line}").ToArray();
        var extract = KnowledgeFileText.Extract("dai.txt", Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        Assert.Equal(3, extract.Parts.Count);
        Assert.False(extract.Truncated);
        Assert.All(extract.Parts, part => Assert.InRange(part.Length, 1, KnowledgePageJson.MaximumBody));
        Assert.Equal(string.Join("\n", lines), string.Join("\n", extract.Parts));

        var huge = string.Join("\n", Enumerable.Range(0, KnowledgeFileText.MaximumTotalText / line.Length + 1000).Select(index => line));
        var cut = KnowledgeFileText.Extract("rat-dai.md", Encoding.UTF8.GetBytes(huge));
        Assert.True(cut.Truncated);
        Assert.Equal(KnowledgeFileText.MaximumParts, cut.Parts.Count);

        var unbroken = KnowledgeFileText.Extract("x.txt", Encoding.UTF8.GetBytes(new string('a', KnowledgePageJson.MaximumBody + 10)));
        Assert.Equal([KnowledgePageJson.MaximumBody, 10], unbroken.Parts.Select(part => part.Length));
    }

    [Fact]
    public void RejectsOversizedFilesAndZipEntries()
    {
        Assert.Equal("tooLarge", Assert.Throws<KnowledgeFileUnreadableException>(() =>
            KnowledgeFileText.Extract("a.txt", new byte[KnowledgeFileText.MaximumFileBytes + 1])).Reason);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = zip.CreateEntry("word/document.xml", CompressionLevel.SmallestSize).Open();
            var zeros = new byte[1024 * 1024];
            for (var index = 0; index < 101; index++) stream.Write(zeros);
        }
        Assert.Equal("tooLarge", Assert.Throws<KnowledgeFileUnreadableException>(() => KnowledgeFileText.Extract("bomb.docx", buffer.ToArray())).Reason);
    }

    [Fact]
    public void CleansFileNamesForTitles()
    {
        Assert.Equal("Báo cáo.pdf", KnowledgeFileText.CleanFileName("../x/\u0007Báo cáo.pdf"));
        Assert.Equal("Tài liệu", KnowledgeFileText.Extract(".txt", "a"u8.ToArray()).Title);
    }
}
