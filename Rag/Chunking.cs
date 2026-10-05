using Microsoft.ML.Tokenizers;

namespace BimSAgentApp.Rag;

public sealed class RagTokenizer
{
    private readonly TiktokenTokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");
    public int Count(string text) => _tokenizer.CountTokens(text);

    public IEnumerable<(int Start, int End)> Windows(string text, int size, int overlap)
    {
        if (size < 8 || overlap < 0 || overlap >= size) throw new ArgumentException("Некорректные size/overlap.");
        var start = 0;
        while (start < text.Length)
        {
            var length = _tokenizer.GetIndexByTokenCount(text.AsSpan(start), size, out _, out _);
            // Token boundaries can land between UTF-16 surrogate pairs.
            if (start + length < text.Length && length > 0 && char.IsHighSurrogate(text[start + length - 1])) length--;
            if (length <= 0) throw new InvalidDataException("Не удалось выделить текстовое окно.");
            var end = start + length;
            yield return (start, end);
            if (end == text.Length) yield break;
            var advance = _tokenizer.GetIndexByTokenCount(text.AsSpan(start, length), size - overlap, out _, out _);
            advance = Math.Min(advance, length);
            if (advance > 0 && char.IsHighSurrogate(text[start + advance - 1])) advance--;
            start += advance > 0 ? advance : length;
        }
    }
}

public abstract class ChunkerBase(RagTokenizer tokenizer, int size = 600, int overlap = 100) : IChunker
{
    public abstract string Strategy { get; }
    public abstract IReadOnlyList<RagChunk> Split(ExtractedDocument document);
    protected readonly RagTokenizer Tokenizer = tokenizer;
    protected readonly int Size = size;
    protected readonly int Overlap = overlap;

    protected IEnumerable<RagChunk> PackBlocks(ExtractedDocument document, IReadOnlyList<DocumentBlock> blocks)
    {
        var pack = new List<DocumentBlock>();
        var parent = RagDefaults.Hash($"v3|{document.Source}|{document.ContentHash}|{Strategy}|{blocks[0].Ordinal}");
        foreach (var block in blocks)
        {
            if (pack.Count > 0 && Tokenizer.Count(string.Join("\n\n", pack.Select(b => b.Text).Append(block.Text))) > Size)
            {
                foreach (var chunk in SplitBlocks(document, pack)) yield return chunk with { ParentBlockId = parent };
                pack.Clear();
            }
            if (Tokenizer.Count(block.Text) > Size)
            {
                // Only an oversized atomic block uses overlapping windows.
                foreach (var chunk in SplitBlocks(document, [block])) yield return chunk with { ParentBlockId = parent };
            }
            else pack.Add(block);
        }
        if (pack.Count > 0) foreach (var chunk in SplitBlocks(document, pack)) yield return chunk with { ParentBlockId = parent };
    }

    protected IEnumerable<RagChunk> SplitBlocks(ExtractedDocument document, IReadOnlyList<DocumentBlock> blocks)
    {
        var text = string.Join("\n\n", blocks.Select(b => b.Text));
        var offsets = new List<(DocumentBlock Block, int Start, int End)>();
        var position = 0;
        foreach (var block in blocks)
        {
            offsets.Add((block, position, position + block.Text.Length));
            position += block.Text.Length + 2;
        }
        foreach (var (start, end) in Tokenizer.Windows(text, Size, Overlap))
        {
            var value = text[start..end];
            if (string.IsNullOrWhiteSpace(value)) continue;
            var covered = offsets.Where(b => b.Start < end && b.End > start).Select(b => b.Block).ToArray();
            var section = string.Join("; ", covered.Select(b => b.Section).Distinct());
            var names = covered.SelectMany(b => b.FamilyNames).Distinct(StringComparer.Ordinal).ToArray();
            // Persist exactly what was embedded, including inherited section/family context.
            var owners = covered.Select(b => b.OwnerFamilyId).Distinct().ToArray();
            var owner = owners.Length == 1 ? owners[0] : null;
            var family = document.Families.FirstOrDefault(f => f.FamilyId == owner);
            var embeddingInput = EmbeddingInputBuilder.Build(value, section, names, family);
            var parent = RagDefaults.Hash($"v2|{document.Source}|{document.ContentHash}|{Strategy}|{blocks[0].Ordinal}");
            var id = RagDefaults.Hash($"v2|{document.Source}|{document.ContentHash}|{Strategy}|{Size}|{Overlap}|{blocks[0].Ordinal}|{start}|{end}|{value}");
            yield return new(document.Source, document.Title, document.File, section, id, Strategy,
                value, embeddingInput, [], RagDefaults.EmbeddingModel, Tokenizer.Count(value), names,
                covered.Select(b => b.Ordinal).ToArray())
            {
                ParentBlockId = parent, OwnerFamilyId = owner,
                FamilyPurpose = family?.Purpose ?? "",
                PurposeBlockIds = family == null ? [] : document.Blocks.Where(b => b.OwnerFamilyId == owner && b.Warnings.Length == 0
                    && family.Purpose.Contains(b.Text, StringComparison.Ordinal)).Select(b => b.BlockId).ToArray(),
                EmbeddingInputHash = RagDefaults.Hash(embeddingInput),
                Warnings = covered.SelectMany(b => b.Warnings).Distinct().ToArray(),
                AssetIds = covered.SelectMany(b => b.AssetIds).Distinct().ToArray(),
                VisualEvidence = offsets.Where(b => b.Block.Kind == "vision_interpretation" && b.Start < end && b.End > start)
                    .Select(b => text[Math.Max(b.Start, start)..Math.Min(b.End, end)]).ToArray(),
                Spans = offsets.Where(b => b.Start < end && b.End > start).Select(b => new EvidenceSpan(b.Block.BlockId,
                    b.Block.OwnerFamilyId, Math.Max(b.Start, start) - start, Math.Min(b.End, end) - start,
                    Math.Max(start - b.Start, 0), Math.Min(end, b.End) - b.Start)).ToArray()
            };
        }
    }
}

public sealed class FixedWindowChunker(RagTokenizer tokenizer, int size = 600, int overlap = 100)
    : ChunkerBase(tokenizer, size, overlap)
{
    public override string Strategy => "fixed";
    public override IReadOnlyList<RagChunk> Split(ExtractedDocument document) => SplitBlocks(document, document.Blocks).ToArray();
}

public sealed class StructuralChunker(RagTokenizer tokenizer, int size = 2000, int overlap = 100)
    : ChunkerBase(tokenizer, size, overlap)
{
    public override string Strategy => "structural";
    public override IReadOnlyList<RagChunk> Split(ExtractedDocument document)
    {
        var result = new List<RagChunk>();
        var group = new List<DocumentBlock>();
        foreach (var block in document.Blocks)
        {
            // Collect the entire family before splitting: subheadings and table rows are not boundaries.
            var boundary = group.Count > 0 && (!block.FamilyNames.SequenceEqual(group[0].FamilyNames)
                || block.Kind == "vision_interpretation" || group[0].Kind == "vision_interpretation"
                || (block.FamilyNames.Length == 0 && (block.Kind == "heading" || block.Section != group[0].Section))
                || (block.Kind == "heading" && block.FamilyNames.Contains(block.Text, StringComparer.Ordinal)));
            if (boundary) { result.AddRange(PackBlocks(document, group)); group.Clear(); }
            group.Add(block);
        }
        if (group.Count > 0) result.AddRange(PackBlocks(document, group));
        return result;
    }
}
