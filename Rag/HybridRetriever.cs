using System.Text.RegularExpressions;

namespace BimSAgentApp.Rag;

public static class SearchLanguage
{
    private static readonly HashSet<string> Stop = new("как какой какая какие какое для про при это что чего где когда нужно можно надо есть или чем почему который которого чтобы этого этой если семейство семейства revit the and with what how подходят используют моделирования".Split(' '));
    public static string[] Tokens(string text) => Regex.Matches(text.ToLowerInvariant().Replace('ё', 'е'), @"[\p{L}\p{N}_№.+\-]+")
        .Select(m => m.Value.Trim('.', '-')).Where(w => w.Length > 0 && !Stop.Contains(w)).Select(Stem).ToArray();
    private static string Stem(string word)
    {
        if (word.Contains('_') || word.Any(char.IsDigit) || word.Length < 5 || word is "нельзя") return word;
        return Regex.Replace(word, @"(?:иями|ями|ами|ого|ему|ому|ыми|ими|ая|яя|ое|ее|ые|ие|ой|ый|ий|ей|ов|ев|ам|ям|ах|ях|ом|ем|ы|и|а|я|у|ю|е)$", "");
    }
}

public sealed class HybridRetriever(IReranker? reranker = null) : IRetriever
{
    private readonly IReranker _reranker = reranker ?? new Bm25Reranker();
    public IReadOnlyList<RetrievalHit> Search(RagIndex index, float[] query, int topK) => new CosineRetriever().Search(index, query, topK);
    public RetrievalSelection Retrieve(RagIndex index, float[] query, string question, int? limit) =>
        Finish(index, _reranker.Rerank(question, Candidates(index, query, question, limit)), limit);
    public async Task<RetrievalSelection> RetrieveAsync(RagIndex index, float[] query, string question, int? limit, CancellationToken cancellationToken)
        => Finish(index, await _reranker.RerankAsync(question, Candidates(index, query, question, limit), cancellationToken), limit);

    private static RetrievalHit[] Candidates(RagIndex index, float[] query, string question, int? limit)
    {
        var names = index.Families.Select(f => f.CanonicalName).Concat(index.Chunks.SelectMany(c => c.FamilyNames))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(n => FamilyIdentity.Contains(question, n)).ToArray();
        var terms = SearchLanguage.Tokens(question).Distinct().ToArray();
        var docs = index.Chunks.Select(c => SearchLanguage.Tokens(c.EmbeddingInput)).ToArray();
        var average = Math.Max(1, docs.Average(d => d.Length));
        var idf = terms.ToDictionary(t => t, t => Math.Log(1 + (docs.Length - docs.Count(d => d.Contains(t)) + 0.5)
            / (docs.Count(d => d.Contains(t)) + 0.5)));
        var all = index.Chunks.Select((chunk, i) =>
        {
            var frequencies = docs[i].GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
            var lexical = terms.Sum(t =>
            {
                var tf = frequencies.GetValueOrDefault(t);
                return idf[t] * tf * 2.2 / (tf + 1.2 * (0.25 + 0.75 * docs[i].Length / average));
            });
            var exact = chunk.FamilyNames.Length == 1 && names.Contains(chunk.FamilyNames[0], StringComparer.OrdinalIgnoreCase);
            return new RetrievalHit(chunk, CosineRetriever.Similarity(query, chunk.Embedding))
            { LexicalScore = lexical, ExactTerms = terms.Where(frequencies.ContainsKey).ToArray(), ExactFamilyMatch = exact,
                ExactFamilyInText = exact && names.Any(n => FamilyIdentity.Contains(chunk.Text, n)), Selected = false };
        }).ToArray();
        var dense = all.OrderByDescending(h => h.SimilarityScore).ThenBy(h => h.Chunk.ChunkId, StringComparer.Ordinal)
            .Select((h, i) => (h.Chunk.ChunkId, Rank: i + 1)).ToDictionary(x => x.ChunkId, x => x.Rank);
        var lexicalRanks = all.Where(h => h.LexicalScore > 0).OrderByDescending(h => h.LexicalScore)
            .ThenBy(h => h.Chunk.ChunkId, StringComparer.Ordinal).Select((h, i) => (h.Chunk.ChunkId, Rank: i + 1)).ToDictionary(x => x.ChunkId, x => x.Rank);
        return all.Select(h => h with
        { DenseRank = dense[h.Chunk.ChunkId], LexicalRank = lexicalRanks.GetValueOrDefault(h.Chunk.ChunkId),
            FusionScore = 1.0 / (60 + dense[h.Chunk.ChunkId]) + (lexicalRanks.TryGetValue(h.Chunk.ChunkId, out var r) ? 1.0 / (60 + r) : 0) })
            .Where(h => h.DenseRank <= Math.Max(60, limit ?? 0) || h.LexicalRank is > 0 and <= 40 || h.ExactFamilyMatch)
            .OrderByDescending(h => h.FusionScore).ThenBy(h => h.Chunk.ChunkId, StringComparer.Ordinal).ToArray();
    }

    private static RetrievalSelection Finish(RagIndex index, IReadOnlyList<RetrievalHit> ranked, int? limit)
    {
        var exactQuery = ranked.Any(h => h.ExactFamilyMatch);
        var eligible = new List<RetrievalHit>(); var rejected = new List<RetrievalHit>();
        var semantic = ranked.Any(h => h.RerankerMode.StartsWith("cross_encoder:", StringComparison.Ordinal));
        var scores = ranked.Where(h => semantic || h.LexicalScore == 0).Select(h => semantic ? h.RerankScore ?? 0 : h.SimilarityScore)
            .OrderDescending().Take(20).ToArray();
        var cutoff = semantic ? 0.0 : 0.25;
        // Gap supplements an absolute floor; scores are not probabilities.
        var largestGap = semantic ? 2.0 : 0.15;
        for (var i = 1; i < scores.Length; i++)
            if (scores[i - 1] - scores[i] > largestGap) { largestGap = scores[i - 1] - scores[i]; cutoff = Math.Max(cutoff, scores[i - 1]); }
        foreach (var hit in ranked)
        {
            string? reason = hit.Chunk.Warnings.Any(w => w.StartsWith("source_conflict", StringComparison.Ordinal)) ? "source_conflict"
                : exactQuery && !hit.ExactFamilyMatch ? "different_owner"
                : semantic && hit.RerankScore < cutoff ? "threshold"
                : !semantic && hit.SimilarityScore < cutoff && hit.LexicalScore <= 0 ? "threshold" : null;
            if (reason == null && eligible.Any(h => Duplicate(h.Chunk, hit.Chunk))) reason = "duplicate";
            if (reason != null) rejected.Add(hit with { Selected = false, SelectionReason = reason }); else eligible.Add(hit);
        }
        string Group(RetrievalHit h) => h.Chunk.OwnerFamilyId ?? (h.Chunk.FamilyNames.Length == 1 ? h.Chunk.FamilyNames[0] : "general:" + h.Chunk.Section);
        var groups = eligible.GroupBy(Group).Select(g => g.ToArray()).ToArray();
        var ordered = new List<RetrievalHit>();
        for (var depth = 0; depth < (exactQuery ? 8 : 2); depth++)
            foreach (var group in groups) if (group.Length > depth) ordered.Add(group[depth]);
        var selected = ordered.Take(limit ?? 8).Select(h => h.Chunk.ChunkId).ToHashSet();
        return new(ordered.Concat(eligible.Where(h => !ordered.Contains(h))).Select(h => h with
        { Selected = selected.Contains(h.Chunk.ChunkId), SelectionReason = selected.Contains(h.Chunk.ChunkId) ? "selected" : "family_limit" })
            .Concat(rejected).ToArray(), index.Chunks.Length);
    }

    private static bool Duplicate(RagChunk a, RagChunk b)
    {
        if (a.OwnerFamilyId != b.OwnerFamilyId || !a.FamilyNames.SequenceEqual(b.FamilyNames)) return false;
        var left = SearchLanguage.Tokens(a.Text); var right = SearchLanguage.Tokens(b.Text);
        if (left.SequenceEqual(right)) return true;
        string[] Critical(string[] words) => words.Where(w => w.Any(char.IsDigit) || w is "не" or "нельзя" or "без").ToArray();
        if (!Critical(left).SequenceEqual(Critical(right))) return false;
        HashSet<string> Shingles(string[] words) => Enumerable.Range(0, Math.Max(0, words.Length - 2))
            .Select(i => string.Join(' ', words.Skip(i).Take(3))).ToHashSet();
        var x = Shingles(left); var y = Shingles(right);
        if (x.Count < 10 || y.Count < 10) return false;
        var intersection = x.Intersect(y).Count();
        return (double)intersection / (x.Count + y.Count - intersection) >= 0.88;
    }
}
