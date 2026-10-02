using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace BimSAgentApp.Rag;

public sealed class DocxDocumentExtractor
{
    // Conservative evidence: explicit field, or an entire heading/cell that looks like a family name.
    private static readonly Regex FamilyName = new(@"^[\p{L}\p{N}][\p{L}\p{N}_№.+()\-]*_[\p{L}\p{N}_№.+()\-]+$", RegexOptions.Compiled);
    private static readonly Regex FamilyField = new(@"(?:Имя|Название)\s+семейства\s*[:：]\s*([^\r\n;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ExtractedDocument Extract(string path)
    {
        if (!Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Ожидается документ .docx.");
        var fullPath = Path.GetFullPath(path);
        // Read once: the hash and extracted text always describe the same document snapshot.
        var bytes = System.IO.File.ReadAllBytes(fullPath);
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        // Load only text-bearing XML parts into the SDK DOM. WordprocessingDocument eagerly resolves
        // every media relationship; the actual source contains dangling image links unrelated to its text.
        XElement? ReadPart(string part)
        {
            var entry = archive.GetEntry(part);
            if (entry == null) return null;
            using var partStream = entry.Open();
            using var reader = XmlReader.Create(partStream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XElement.Load(reader, LoadOptions.PreserveWhitespace);
        }
        string? RelatedPart(string owner, string relationshipType)
        {
            var slash = owner.LastIndexOf('/');
            var relationships = owner.Length == 0 ? "_rels/.rels" :
                owner[..(slash + 1)] + "_rels/" + owner[(slash + 1)..] + ".rels";
            var relation = ReadPart(relationships)?.Elements().FirstOrDefault(e =>
                ((string?)e.Attribute("Type"))?.EndsWith("/" + relationshipType, StringComparison.Ordinal) == true
                && (string?)e.Attribute("TargetMode") != "External");
            var target = (string?)relation?.Attribute("Target");
            return target == null ? null : Uri.UnescapeDataString(new Uri(new Uri("http://package/" + owner), target).AbsolutePath.TrimStart('/'));
        }
        var mainPath = RelatedPart("", "officeDocument") ?? throw new InvalidDataException("DOCX не содержит основного документа.");
        var mainXml = ReadPart(mainPath) ?? throw new InvalidDataException("Отсутствует XML основного документа.");
        var body = new Document(mainXml.ToString(SaveOptions.DisableFormatting)).Body ?? throw new InvalidDataException("DOCX не содержит текста.");
        var stylesPath = RelatedPart(mainPath, "styles");
        var stylesXml = stylesPath == null ? null : ReadPart(stylesPath);
        var styleElements = stylesXml == null ? null : new Styles(stylesXml.ToString(SaveOptions.DisableFormatting));
        var styles = styleElements?.Elements<Style>()
            .Where(s => s.StyleId?.Value != null).ToDictionary(s => s.StyleId!.Value!, s => s)
            ?? new Dictionary<string, Style>();
        var sections = new SortedDictionary<int, string>();
        var blocks = new List<DocumentBlock>();
        var families = Array.Empty<string>();
        int ordinal = 0;

        int? HeadingLevel(Paragraph paragraph)
        {
            var direct = paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value;
            if (direct is >= 0 and <= 8) return direct;
            var id = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            var visited = new HashSet<string>();
            while (id != null && visited.Add(id) && styles.TryGetValue(id, out var style))
            {
                var level = style.StyleParagraphProperties?.OutlineLevel?.Val?.Value;
                if (level is >= 0 and <= 8) return level;
                var match = Regex.Match(style.StyleName?.Val?.Value ?? id,
                    @"^(?:heading|заголовок)\s*([1-9])$", RegexOptions.IgnoreCase);
                if (match.Success) return int.Parse(match.Groups[1].Value) - 1;
                id = style.BasedOn?.Val?.Value;
            }
            return null;
        }

        void Walk(OpenXmlElement parent)
        {
            foreach (var element in parent.ChildElements)
            {
                if (element is Paragraph paragraph)
                {
                    var text = ReadText(paragraph).Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var level = HeadingLevel(paragraph);
                    if (level != null)
                    {
                        foreach (var key in sections.Keys.Where(k => k >= level).ToArray()) sections.Remove(key);
                        sections[level.Value] = text;
                        families = sections.Values.Where(IsFamily).Distinct(StringComparer.Ordinal).ToArray();
                    }
                    var explicitNames = FamilyField.Matches(text).Select(m => m.Groups[1].Value.Trim()).ToArray();
                    if (explicitNames.Length > 0) families = explicitNames;
                    blocks.Add(new(++ordinal, Section(), text, level == null ? "paragraph" : "heading", families.ToArray()));
                }
                else if (element is Table table)
                {
                    var rowNumber = 0;
                    int? familyColumn = null;
                    foreach (var row in table.Elements<TableRow>())
                    {
                        var cells = row.Elements<TableCell>().Select(c => string.Join("\n", c.Descendants<Paragraph>()
                            .Select(ReadText).Where(t => !string.IsNullOrWhiteSpace(t)))).ToArray();
                        var text = string.Join(" | ", cells);
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        // Parameter names also contain underscores: only a labelled family column is evidence.
                        if (rowNumber == 0)
                        {
                            var column = Array.FindIndex(cells, c => Regex.IsMatch(c.Trim(),
                                @"^(?:(?:имя|название)\s+)?семейств[оа]$", RegexOptions.IgnoreCase));
                            if (column >= 0) familyColumn = column;
                        }
                        var rowFamilies = familyColumn is { } familyIndex && familyIndex < cells.Length && IsFamily(cells[familyIndex])
                            ? new[] { cells[familyIndex].Trim() } : Array.Empty<string>();
                        blocks.Add(new(++ordinal, Section(), text, "table-row:" + ++rowNumber,
                            rowFamilies.Length == 0 ? families.ToArray() : rowFamilies));
                    }
                }
                else if (element.LocalName != "del") Walk(element); // Content controls and inserted text.
            }
        }

        string Section() => sections.Count == 0 ? "Без раздела" : string.Join(" / ", sections.Values);
        Walk(body);
        if (blocks.Count == 0) throw new InvalidDataException("В DOCX не найден текст. OCR не поддерживается.");
        return new(fullPath, Path.GetFileNameWithoutExtension(path),
            Path.GetFileName(path), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), blocks);
    }

    private static bool IsFamily(string text) => FamilyName.IsMatch(text.Trim());

    private static string ReadText(Paragraph paragraph) => string.Concat(paragraph.Descendants()
        .Where(e => !e.Ancestors().Any(a => a.LocalName == "del"))
        .Select(e => e switch { Text t => t.Text, TabChar => "\t", Break => "\n", _ => "" }));
}
