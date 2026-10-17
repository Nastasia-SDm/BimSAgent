using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BimSAgentApp.Rag;

public static class MarkdownDocumentExtractor
{
    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.+?)\s*$", RegexOptions.Compiled);

    public static ExtractedDocument Extract(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Build(fullPath, [fullPath]);
    }

    public static ExtractedDocument ExtractProjectDocumentation(string projectDirectory)
    {
        var root = Path.GetFullPath(projectDirectory);
        var files = new List<string>();

        var readme = Path.Combine(root, "README.md");
        if (File.Exists(readme)) files.Add(readme);

        var docs = Path.Combine(root, "docs");
        if (Directory.Exists(docs))
            files.AddRange(Directory.EnumerateFiles(docs, "*.md", SearchOption.AllDirectories)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        if (files.Count == 0)
            throw new InvalidDataException("В проекте не найдены README.md или Markdown-файлы в папке docs.");

        return Build(root, files);
    }

    private static ExtractedDocument Build(string source, IReadOnlyList<string> files)
    {
        var blocks = new List<DocumentBlock>();
        var hashInput = new StringBuilder();
        var root = Directory.Exists(source) ? source : Path.GetDirectoryName(source)!;
        var ordinal = 0;

        foreach (var file in files)
        {
            var fullPath = Path.GetFullPath(file);
            var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            var text = File.ReadAllText(fullPath, Encoding.UTF8);
            hashInput.Append(relative).Append('\n').Append(text).Append("\n\0\n");

            var headings = new SortedDictionary<int, string>();
            var paragraph = new List<string>();
            var paragraphStart = 1;
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            string Section() => headings.Count == 0
                ? relative
                : relative + " / " + string.Join(" / ", headings.Values);

            void FlushParagraph(int endLine)
            {
                if (paragraph.Count == 0) return;
                var value = string.Join("\n", paragraph).Trim();
                paragraph.Clear();
                if (value.Length == 0) return;

                blocks.Add(new(++ordinal, Section(), value, "paragraph", [])
                {
                    BlockId = RagDefaults.Hash($"{relative}|{paragraphStart}|{endLine}|{value}"),
                    XmlPart = relative,
                    XmlPath = $"lines/{paragraphStart}-{endLine}"
                });
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var lineNumber = i + 1;
                var line = lines[i];
                var match = Heading.Match(line);
                if (match.Success)
                {
                    FlushParagraph(lineNumber - 1);
                    var level = match.Groups[1].Value.Length - 1;
                    var headingTitle = match.Groups[2].Value.Trim();
                    foreach (var key in headings.Keys.Where(k => k >= level).ToArray()) headings.Remove(key);
                    headings[level] = headingTitle;
                   blocks.Add(new(++ordinal, Section(), headingTitle, "heading", [])
                    {
                       BlockId = RagDefaults.Hash($"{relative}|heading|{lineNumber}|{headingTitle}"),
                        XmlPart = relative,
                        XmlPath = $"line/{lineNumber}",
                        HeadingLevel = level
                    });
                    paragraphStart = lineNumber + 1;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    FlushParagraph(lineNumber - 1);
                    paragraphStart = lineNumber + 1;
                    continue;
                }

                if (paragraph.Count == 0) paragraphStart = lineNumber;
                paragraph.Add(line);
            }

            FlushParagraph(lines.Length);
        }

        if (blocks.Count == 0)
            throw new InvalidDataException("В README/docs не найден текст для индексации.");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput.ToString()))).ToLowerInvariant();
        var title = Directory.Exists(source) ? Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : Path.GetFileNameWithoutExtension(source);
        var fileLabel = Directory.Exists(source) ? "README.md + docs/*.md" : Path.GetFileName(source);
        return new(source, title, fileLabel, hash, blocks);
    }
}
