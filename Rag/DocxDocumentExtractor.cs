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
        var assets = new List<DocumentAsset>();
        string Anchor(OpenXmlElement element) => "/" + string.Join("/", element.Ancestors().Reverse().Append(element)
            .Select(e => e.LocalName + "[" + (1 + (e.Parent?.ChildElements.TakeWhile(x => !ReferenceEquals(x, e))
                .Count(x => x.LocalName == e.LocalName) ?? 0)) + "]"));
        var slash = mainPath.LastIndexOf('/');
        var relations = ReadPart(mainPath[..(slash + 1)] + "_rels/" + mainPath[(slash + 1)..] + ".rels")?
            .Elements().Where(e => e.Attribute("Id") != null).ToDictionary(e => (string)e.Attribute("Id")!, e => e)
            ?? new Dictionary<string, XElement>();
        string[] ReadAssets(OpenXmlElement element)
        {
            var ids = new List<string>();
            foreach (var image in element.Descendants().Where(e => e.LocalName is "blip" or "imagedata"))
            {
                var relationship = image.GetAttributes().FirstOrDefault(a => a.LocalName is "embed" or "id" or "link").Value ?? "";
                relations.TryGetValue(relationship, out var relation);
                var external = (string?)relation?.Attribute("TargetMode") == "External";
                var target = (string?)relation?.Attribute("Target");
                var part = target == null || external ? null : Uri.UnescapeDataString(new Uri(new Uri("http://package/" + mainPath), target).AbsolutePath.TrimStart('/'));
                var entry = part == null ? null : archive.GetEntry(part);
                string? hash = null;
                if (entry != null) { using var media = entry.Open(); hash = Convert.ToHexString(SHA256.HashData(media)).ToLowerInvariant(); }
                var path = Anchor(image);
                var id = RagDefaults.Hash(fullPath + "|" + path + "|" + hash);
                var drawing = image.Ancestors().FirstOrDefault(e => e.LocalName is "drawing" or "pict");
                var properties = drawing?.Descendants().FirstOrDefault(e => e.LocalName == "docPr");
                assets.Add(new(id, relationship, mainPath, path, part, hash,
                    external ? "external" : entry == null ? "missing" : "unprocessed")
                { AltText = properties?.GetAttributes().FirstOrDefault(a => a.LocalName == "descr").Value ?? "" });
                ids.Add(id);
            }
            return ids.ToArray();
        }
        DocumentBlock Locate(DocumentBlock block, OpenXmlElement element, string[] assetIds) => block with
        { BlockId = RagDefaults.Hash(fullPath + "|" + mainPath + "|" + Anchor(element)), XmlPart = mainPath,
            XmlPath = Anchor(element), AssetIds = assetIds };
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
                    var assetIds = ReadAssets(paragraph);
                    if (string.IsNullOrWhiteSpace(text) && assetIds.Length == 0) continue;
                    if (string.IsNullOrWhiteSpace(text)) text = "[Изображение: содержимое ещё не распознано]";
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
                    blocks[^1] = Locate(blocks[^1], paragraph, assetIds) with { HeadingLevel = level };
                }
                else if (element is Table table)
                {
                    var rowNumber = 0;
                    int? familyColumn = null;
                    string[] headers = [];
                    foreach (var row in table.Elements<TableRow>())
                    {
                        var cells = row.Elements<TableCell>().Select(c => string.Join("\n", c.Descendants<Paragraph>()
                            .Select(ReadText).Where(t => !string.IsNullOrWhiteSpace(t)))).ToArray();
                        var text = string.Join(" | ", cells);
                        var assetIds = ReadAssets(row);
                        if (string.IsNullOrWhiteSpace(text) && assetIds.Length == 0) continue;
                        if (string.IsNullOrWhiteSpace(text)) text = "[Изображение в таблице: содержимое ещё не распознано]";
                        // Parameter names also contain underscores: only a labelled family column is evidence.
                        if (rowNumber == 0)
                        {
                            headers = cells;
                            var column = Array.FindIndex(cells, c => Regex.IsMatch(c.Trim(),
                                @"^(?:(?:имя|название)\s+)?семейств[оа]$", RegexOptions.IgnoreCase));
                            if (column >= 0) familyColumn = column;
                        }
                        var rowFamilies = familyColumn is { } familyIndex && familyIndex < cells.Length && IsFamily(cells[familyIndex])
                            ? new[] { cells[familyIndex].Trim() } : Array.Empty<string>();
                        blocks.Add(new(++ordinal, Section(), text, "table-row:" + ++rowNumber,
                            rowFamilies.Length == 0 ? families.ToArray() : rowFamilies));
                        blocks[^1] = Locate(blocks[^1], row, assetIds) with
                        {
                            TableId = RagDefaults.Hash(fullPath + "|" + Anchor(table)), Row = rowNumber,
                            TableHeaders = headers,
                            Cells = row.Elements<TableCell>().Select((c, i) => new TableCellData(i + 1, cells[i],
                                c.TableCellProperties?.GridSpan?.Val?.Value ?? 1,
                                c.TableCellProperties?.VerticalMerge == null ? null : c.TableCellProperties.VerticalMerge.Val?.InnerText ?? "continue", Anchor(c))).ToArray()
                        };
                    }
                }
                else if (element.LocalName != "del") Walk(element); // Content controls and inserted text.
            }
        }

        string Section() => sections.Count == 0 ? "Без раздела" : string.Join(" / ", sections.Values);
        Walk(body);
        if (blocks.Count == 0) throw new InvalidDataException("В DOCX не найден текст. OCR не поддерживается.");
        return new FamilyCardBuilder().Build(new(fullPath, Path.GetFileNameWithoutExtension(path),
            Path.GetFileName(path), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), blocks) { Assets = assets.ToArray() });
    }

    private static bool IsFamily(string text) => FamilyIdentity.HeadingName(text) != null;

    private static string ReadText(Paragraph paragraph) => string.Concat(paragraph.Descendants()
        .Where(e => !e.Ancestors().Any(a => a.LocalName == "del"))
        .Select(e => e switch { Text t => t.Text, TabChar => "\t", Break => "\n", _ => "" }));
}
