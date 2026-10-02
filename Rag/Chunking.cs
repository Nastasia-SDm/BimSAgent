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
            var embeddingInput = section + "\n" + string.Join("; ", names) + "\n" + value;
            var id = RagDefaults.Hash($"v1|{document.Source}|{document.ContentHash}|{Strategy}|{Size}|{Overlap}|{blocks[0].Ordinal}|{start}|{end}|{value}");
            yield return new(document.Source, document.Title, document.File, section, id, Strategy,
                value, embeddingInput, [], RagDefaults.EmbeddingModel, Tokenizer.Count(value), names,
                covered.Select(b => b.Ordinal).ToArray());
        }
    }
}

public sealed class FixedWindowChunker(RagTokenizer tokenizer, int size = 600, int overlap = 100)
    : ChunkerBase(tokenizer, size, overlap)
{
    public override string Strategy => "fixed";
    public override IReadOnlyList<RagChunk> Split(ExtractedDocument document) => SplitBlocks(document, document.Blocks).ToArray();
}

public sealed class StructuralChunker(RagTokenizer tokenizer, int size = 600, int overlap = 100)
    : ChunkerBase(tokenizer, size, overlap)
{
    public override string Strategy => "structural";
    public override IReadOnlyList<RagChunk> Split(ExtractedDocument document)
    {
        var result = new List<RagChunk>();
        var group = new List<DocumentBlock>();
        foreach (var block in document.Blocks)
        {
            var boundary = group.Count > 0 && (block.Kind == "heading" || block.Section != group[0].Section
                || !block.FamilyNames.SequenceEqual(group[0].FamilyNames)
                || Tokenizer.Count(string.Join("\n\n", group.Select(b => b.Text).Append(block.Text))) > Size);
            if (boundary) { result.AddRange(SplitBlocks(document, group)); group.Clear(); }
            group.Add(block);
        }
        if (group.Count > 0) result.AddRange(SplitBlocks(document, group));
        return result;
    }
}
