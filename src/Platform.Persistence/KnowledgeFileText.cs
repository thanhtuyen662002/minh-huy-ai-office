using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Text read out of an uploaded file. Long text is split at line breaks into page-sized parts.
public sealed record KnowledgeFileExtract(string Title, IReadOnlyList<string> Parts, bool Truncated);

// Reason is a stable code the web maps to a Vietnamese message: unsupported, legacy, empty, encrypted, damaged, tooLarge.
public sealed class KnowledgeFileUnreadableException(string reason) : Exception("Knowledge file is unreadable.")
{
    public string Reason { get; } = reason;
}

public static class KnowledgeFileText
{
    public const int MaximumFileBytes = 20 * 1024 * 1024;
    public const int MaximumFileName = 255;
    public const int MaximumParts = 4;
    public const int MaximumTotalText = MaximumParts * KnowledgePageJson.MaximumBody;
    private const long MaximumUnpackedEntry = 100L * 1024 * 1024;
    private const int MaximumSheetColumns = 200;
    private static readonly string[] Supported = [".pdf", ".docx", ".xlsx", ".pptx", ".txt", ".md", ".markdown", ".csv", ".tsv"];
    private static readonly string[] Legacy = [".doc", ".xls", ".ppt", ".rtf", ".odt", ".ods"];

    static KnowledgeFileText() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static bool IsSupported(string fileName) => Supported.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    public static KnowledgeFileExtract Extract(string fileName, byte[] bytes, CancellationToken cancellationToken = default)
    {
        var name = CleanFileName(fileName);
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (Legacy.Contains(extension)) throw new KnowledgeFileUnreadableException("legacy");
        if (!Supported.Contains(extension)) throw new KnowledgeFileUnreadableException("unsupported");
        if (bytes.Length > MaximumFileBytes) throw new KnowledgeFileUnreadableException("tooLarge");
        if (bytes.Length == 0) throw new KnowledgeFileUnreadableException("empty");
        var output = new TextOutput();
        try
        {
            switch (extension)
            {
                case ".pdf": Pdf(bytes, output, cancellationToken); break;
                case ".docx": Docx(bytes, output, cancellationToken); break;
                case ".xlsx": Xlsx(bytes, output, cancellationToken); break;
                case ".pptx": Pptx(bytes, output, cancellationToken); break;
                default: output.Append(Decode(bytes)); break;
            }
        }
        catch (Exception error) when (error is not (KnowledgeFileUnreadableException or OperationCanceledException or TextOutput.FullException))
        {
            throw new KnowledgeFileUnreadableException(error is PdfDocumentEncryptedException ? "encrypted" : "damaged");
        }
        catch (TextOutput.FullException) { }
        var text = Normalize(output.ToString());
        if (text.Length == 0) throw new KnowledgeFileUnreadableException("empty");
        var truncated = output.Full || text.Length > MaximumTotalText;
        if (text.Length > MaximumTotalText) text = text[..SafeCut(text, MaximumTotalText)];
        var title = Path.GetFileNameWithoutExtension(name).Trim();
        if (title.Length > KnowledgePageJson.MaximumName) title = title[..SafeCut(title, KnowledgePageJson.MaximumName)].Trim();
        var parts = Split(text, KnowledgePageJson.MaximumBody);
        if (parts.Count > MaximumParts) { parts = parts.Take(MaximumParts).ToArray(); truncated = true; }
        return new(title.Length == 0 ? "Tài liệu" : title, parts, truncated);
    }

    public static string CleanFileName(string fileName)
    {
        // Browsers send the bare name; drop any path a client adds and every control character.
        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var clean = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
            if (!Rune.IsControl(rune) && rune.Value != 0xFFFD) clean.Append(rune.ToString());
        return clean.ToString().Trim();
    }

    // Splits at the last line break inside each part; a single huge line is cut where it has to be.
    internal static IReadOnlyList<string> Split(string text, int maximum)
    {
        var parts = new List<string>();
        var start = 0;
        while (text.Length - start > maximum)
        {
            var end = text.LastIndexOf('\n', start + maximum - 1, maximum);
            var cut = end > start + maximum / 2 ? end + 1 : start + SafeCut(text.AsSpan(start), maximum);
            parts.Add(text[start..cut].TrimEnd());
            start = cut;
            while (start < text.Length && text[start] == '\n') start++;
        }
        if (start < text.Length) parts.Add(text[start..]);
        return parts.Where(part => part.Length > 0).ToArray();
    }

    private static int SafeCut(ReadOnlySpan<char> text, int length) => char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;

    internal static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var blank = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = new StringBuilder(raw.Length);
            foreach (var rune in raw.EnumerateRunes())
            {
                if (rune.Value == '\t') line.Append('\t');
                else if (rune.Value == 0xA0) line.Append(' ');
                else if (!Rune.IsControl(rune) && rune.Value is not (0xFEFF or 0xFFFD)) line.Append(rune.ToString());
            }
            var value = line.ToString().TrimEnd();
            if (value.Length == 0) { blank++; continue; }
            if (builder.Length > 0) builder.Append(blank > 0 ? "\n\n" : "\n");
            builder.Append(value);
            blank = 0;
        }
        return builder.ToString();
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        // Older Vietnamese text files are often saved in Windows-1258.
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1258).GetString(bytes); }
    }

    private static void Pdf(byte[] bytes, TextOutput output, CancellationToken cancellationToken)
    {
        using var document = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Append(ContentOrderTextExtractor.GetText(page));
            output.Append("\n\n");
        }
    }

    private static ZipArchive Open(byte[] bytes)
    {
        if (bytes is not [0x50, 0x4B, ..]) throw new KnowledgeFileUnreadableException("damaged");
        return new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
    }

    private static XmlReader? Entry(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path.TrimStart('/'));
        if (entry is null) return null;
        if (entry.Length > MaximumUnpackedEntry) throw new KnowledgeFileUnreadableException("tooLarge");
        var buffer = new MemoryStream();
        using (var stream = entry.Open())
        {
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaximumUnpackedEntry) throw new KnowledgeFileUnreadableException("tooLarge");
            }
        }
        buffer.Position = 0;
        return XmlReader.Create(buffer, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = true
        });
    }

    private const string RelationshipNamespace = "/officeDocument/2006/relationships";

    private static string? Attribute(XmlReader reader, string localName, bool relationship = false)
    {
        if (!reader.HasAttributes) return null;
        for (var index = 0; index < reader.AttributeCount; index++)
        {
            reader.MoveToAttribute(index);
            if (reader.LocalName == localName && (!relationship || reader.NamespaceURI.EndsWith(RelationshipNamespace, StringComparison.Ordinal)))
            { var value = reader.Value; reader.MoveToElement(); return value; }
        }
        reader.MoveToElement();
        return null;
    }

    private static bool IsText(XmlReader reader) => reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA
        or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace;

    // Word: paragraphs in order, headings marked with #, list items with -, table rows as "a | b | c".
    private static void Docx(byte[] bytes, TextOutput output, CancellationToken cancellationToken)
    {
        using var zip = Open(bytes);
        var styles = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var reader = Entry(zip, "word/styles.xml"))
        {
            string? id = null;
            while (reader is not null && reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "style") id = Attribute(reader, "styleId");
                else if (reader.LocalName == "name" && id is not null) styles[id] = (Attribute(reader, "val") ?? "").ToLowerInvariant();
            }
        }
        using var document = Entry(zip, "word/document.xml") ?? throw new KnowledgeFileUnreadableException("damaged");
        var paragraph = new StringBuilder();
        var prefix = "";
        var cells = new List<string>();
        var cell = new StringBuilder();
        int tableDepth = 0, count = 0;
        var inText = false;
        void EndParagraph()
        {
            var text = paragraph.ToString().Trim();
            paragraph.Clear();
            if (tableDepth > 0) { if (text.Length > 0) cell.Append(cell.Length > 0 ? " " : "").Append(text); }
            else output.Append(text.Length > 0 ? prefix + text + "\n" : "\n");
            prefix = "";
        }
        while (document.Read())
        {
            if (++count % 4096 == 0) cancellationToken.ThrowIfCancellationRequested();
            if (inText && IsText(document)) { paragraph.Append(document.Value); continue; }
            if (document.NodeType == XmlNodeType.Element)
            {
                switch (document.LocalName)
                {
                    case "p": paragraph.Clear(); prefix = ""; if (document.IsEmptyElement) EndParagraph(); break;
                    case "pStyle":
                        var style = Attribute(document, "val") ?? "";
                        var name = styles.GetValueOrDefault(style, style.ToLowerInvariant());
                        if (name is "title" or "heading 1" or "heading1") prefix = "# ";
                        else if (name.StartsWith("heading", StringComparison.Ordinal)) prefix = "## ";
                        else if (name.StartsWith("list", StringComparison.Ordinal)) prefix = "- ";
                        break;
                    case "numPr": if (prefix.Length == 0) prefix = "- "; break;
                    case "t": inText = !document.IsEmptyElement; break;
                    case "tab": paragraph.Append('\t'); break;
                    case "br" or "cr": paragraph.Append(' '); break;
                    case "tbl": if (tableDepth++ == 0) output.Append("\n"); break;
                    case "tc": if (tableDepth == 1) cell.Clear(); break;
                }
                continue;
            }
            if (document.NodeType != XmlNodeType.EndElement) continue;
            switch (document.LocalName)
            {
                case "t": inText = false; break;
                case "p": EndParagraph(); break;
                case "tc": if (tableDepth == 1) { cells.Add(cell.ToString()); cell.Clear(); } break;
                case "tr":
                    if (tableDepth == 1)
                    {
                        while (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
                        if (cells.Count > 0) output.Append(string.Join(" | ", cells) + "\n");
                        cells.Clear();
                    }
                    break;
                case "tbl": if (--tableDepth == 0) output.Append("\n"); break;
            }
        }
    }

    // Excel: every sheet under its own heading, one row per line with cells joined by " | ".
    private static void Xlsx(byte[] bytes, TextOutput output, CancellationToken cancellationToken)
    {
        using var zip = Open(bytes);
        var targets = Relationships(zip, "xl/_rels/workbook.xml.rels", "xl/");
        var sheets = new List<(string Name, string Path)>();
        using (var workbook = Entry(zip, "xl/workbook.xml") ?? throw new KnowledgeFileUnreadableException("damaged"))
            while (workbook.Read())
                if (workbook.NodeType == XmlNodeType.Element && workbook.LocalName == "sheet"
                    && targets.TryGetValue(Attribute(workbook, "id", relationship: true) ?? "", out var path))
                    sheets.Add((Attribute(workbook, "name") ?? "", path));
        var shared = new List<string>();
        using (var strings = Entry(zip, "xl/sharedStrings.xml"))
        {
            var current = new StringBuilder();
            int phonetic = 0;
            var inText = false;
            while (strings is not null && strings.Read())
            {
                if (inText && IsText(strings)) { if (phonetic == 0) current.Append(strings.Value); continue; }
                if (strings.NodeType == XmlNodeType.Element)
                {
                    if (strings.LocalName == "si") { current.Clear(); if (strings.IsEmptyElement) shared.Add(""); }
                    else if (strings.LocalName == "rPh" && !strings.IsEmptyElement) phonetic++;
                    else if (strings.LocalName == "t") inText = !strings.IsEmptyElement;
                    continue;
                }
                if (strings.NodeType != XmlNodeType.EndElement) continue;
                if (strings.LocalName == "t") inText = false;
                else if (strings.LocalName == "rPh") phonetic--;
                else if (strings.LocalName == "si") shared.Add(current.ToString());
            }
        }
        var dates = DateStyles(zip);
        foreach (var (name, path) in sheets)
        {
            using var sheet = Entry(zip, path);
            if (sheet is null) continue;
            output.Append($"# {name}\n");
            var row = new SortedDictionary<int, string>();
            int column = 0, style = 0, count = 0;
            string? type = null;
            var value = new StringBuilder();
            var inValue = false;
            while (sheet.Read())
            {
                if (++count % 4096 == 0) cancellationToken.ThrowIfCancellationRequested();
                if (inValue && IsText(sheet)) { value.Append(sheet.Value); continue; }
                if (sheet.NodeType == XmlNodeType.Element)
                {
                    switch (sheet.LocalName)
                    {
                        case "row": row.Clear(); column = 0; break;
                        case "c":
                            var reference = Attribute(sheet, "r");
                            column = reference is null ? column + 1 : ColumnIndex(reference);
                            type = Attribute(sheet, "t");
                            style = int.TryParse(Attribute(sheet, "s"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
                            value.Clear();
                            break;
                        case "v" or "t": inValue = !sheet.IsEmptyElement; break;
                    }
                    continue;
                }
                if (sheet.NodeType != XmlNodeType.EndElement) continue;
                if (sheet.LocalName is "v" or "t") inValue = false;
                else if (sheet.LocalName == "c" && column is > 0 and <= MaximumSheetColumns)
                {
                    var text = Cell(value.ToString(), type, dates.Contains(style), shared).Replace('\n', ' ').Trim();
                    if (text.Length > 0) row[column] = text;
                }
                else if (sheet.LocalName == "row" && row.Count > 0)
                {
                    var last = row.Keys.Max();
                    output.Append(string.Join(" | ", Enumerable.Range(1, last).Select(index => row.GetValueOrDefault(index, ""))) + "\n");
                    row.Clear();
                }
            }
            output.Append("\n");
        }
    }

    private static string Cell(string raw, string? type, bool date, List<string> shared) => type switch
    {
        "s" => int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < shared.Count ? shared[index] : "",
        "b" => raw == "1" ? "TRUE" : "FALSE",
        "inlineStr" or "str" or "e" => raw,
        _ when date && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 0 and < 2958466 =>
            DateTime.FromOADate(serial) is var at && at.TimeOfDay == TimeSpan.Zero
                ? at.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : at.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
        _ => raw,
    };

    internal static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var character in reference)
        {
            if (character is < 'A' or > 'Z') break;
            index = index * 26 + character - 'A' + 1;
            if (index > MaximumSheetColumns) return MaximumSheetColumns + 1;
        }
        return index;
    }

    // Cell styles whose number format shows a date, so serial numbers print as dd/MM/yyyy.
    private static HashSet<int> DateStyles(ZipArchive zip)
    {
        var custom = new Dictionary<int, bool>();
        var styles = new HashSet<int>();
        using var reader = Entry(zip, "xl/styles.xml");
        var inCellXfs = false;
        var position = 0;
        while (reader is not null && reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "cellXfs") inCellXfs = false;
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName == "numFmt" && int.TryParse(Attribute(reader, "numFmtId"), CultureInfo.InvariantCulture, out var id))
                custom[id] = IsDateFormat(Attribute(reader, "formatCode") ?? "");
            else if (reader.LocalName == "cellXfs") inCellXfs = !reader.IsEmptyElement;
            else if (reader.LocalName == "xf" && inCellXfs)
            {
                var format = int.TryParse(Attribute(reader, "numFmtId"), CultureInfo.InvariantCulture, out var f) ? f : 0;
                if (format is >= 14 and <= 22 or >= 45 and <= 47 || custom.GetValueOrDefault(format)) styles.Add(position);
                position++;
            }
        }
        return styles;
    }

    internal static bool IsDateFormat(string code)
    {
        var plain = new StringBuilder();
        var quoted = false;
        var bracket = false;
        foreach (var character in code)
        {
            if (character == '"') quoted = !quoted;
            else if (!quoted && character == '[') bracket = true;
            else if (!quoted && character == ']') bracket = false;
            else if (!quoted && !bracket) plain.Append(char.ToLowerInvariant(character));
        }
        var text = plain.ToString();
        return text.Contains('y') || text.Contains('d');
    }

    // PowerPoint: each slide's text under "# Trang chiếu N".
    private static void Pptx(byte[] bytes, TextOutput output, CancellationToken cancellationToken)
    {
        using var zip = Open(bytes);
        var targets = Relationships(zip, "ppt/_rels/presentation.xml.rels", "ppt/");
        var slides = new List<string>();
        using (var presentation = Entry(zip, "ppt/presentation.xml") ?? throw new KnowledgeFileUnreadableException("damaged"))
            while (presentation.Read())
                if (presentation.NodeType == XmlNodeType.Element && presentation.LocalName == "sldId"
                    && targets.TryGetValue(Attribute(presentation, "id", relationship: true) ?? "", out var path))
                    slides.Add(path);
        var number = 0;
        foreach (var path in slides)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var slide = Entry(zip, path);
            if (slide is null) continue;
            output.Append($"# Trang chiếu {++number}\n");
            var paragraph = new StringBuilder();
            var inText = false;
            while (slide.Read())
            {
                if (inText && IsText(slide)) { paragraph.Append(slide.Value); continue; }
                if (slide.NodeType == XmlNodeType.Element && slide.LocalName == "t") inText = !slide.IsEmptyElement;
                else if (slide.NodeType == XmlNodeType.EndElement && slide.LocalName == "t") inText = false;
                else if (slide.NodeType == XmlNodeType.EndElement && slide.LocalName == "p")
                {
                    var text = paragraph.ToString().Trim();
                    if (text.Length > 0) output.Append(text + "\n");
                    paragraph.Clear();
                }
            }
            output.Append("\n");
        }
    }

    private static Dictionary<string, string> Relationships(ZipArchive zip, string path, string root)
    {
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = Entry(zip, path);
        while (reader is not null && reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship") continue;
            var id = Attribute(reader, "Id");
            var target = Attribute(reader, "Target");
            if (id is null || target is null || target.Contains("://", StringComparison.Ordinal)) continue;
            targets[id] = target.StartsWith('/') ? target.TrimStart('/') : root + target;
        }
        return targets;
    }

    // Collects text and stops reading once a little more than the page limit allows has arrived.
    private sealed class TextOutput
    {
        private readonly StringBuilder builder = new();
        public bool Full { get; private set; }
        public void Append(string text)
        {
            if (builder.Length + text.Length > MaximumTotalText + 4096)
            {
                builder.Append(text.AsSpan(0, Math.Max(0, MaximumTotalText + 4096 - builder.Length)));
                Full = true;
                throw new FullException();
            }
            builder.Append(text);
        }
        public override string ToString() => builder.ToString();
        public sealed class FullException : Exception;
    }
}
