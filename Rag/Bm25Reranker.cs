using System.Text.RegularExpressions;

namespace BimSAgentApp.Rag;

public interface IReranker
{
    IReadOnlyList<RetrievalHit> Rerank(string question, IReadOnlyList<RetrievalHit> candidates);
    Task<IReadOnlyList<RetrievalHit>> RerankAsync(string question, IReadOnlyList<RetrievalHit> candidates, CancellationToken cancellationToken)
        => Task.FromResult(Rerank(question, candidates));
}

// Local, query-dependent second stage; no HTTP, embeddings or generation.
public sealed class Bm25Reranker : IReranker
{
    private static readonly HashSet<string> StopWords = new(
        "как какой какая какие какое для про при это что где когда нужно можно надо есть или чем почему который которого чтобы этого этой если the and with what how".Split(' '));
    private static string[] Tokens(string text) => Regex.Matches(text.ToLowerInvariant().Replace('ё', 'е'), @"[\p{L}\p{N}_]+")
        .Select(m => m.Value).Where(w => w.Length > 2 && !StopWords.Contains(w)).ToArray();

    public IReadOnlyList<RetrievalHit> Rerank(string question, IReadOnlyList<RetrievalHit> candidates)
    {
        if (candidates.Count == 0) return [];
        if (candidates.All(h => h.FusionScore > 0))
            return candidates.Select(h => h with { RerankScore = h.FusionScore, RerankerMode = "lexical_fallback" })
                .OrderByDescending(h => h.RerankScore).ThenBy(h => h.Chunk.ChunkId, StringComparer.Ordinal).ToArray();
        var terms = Tokens(question).Distinct().ToArray();
        var docs = candidates.Select(h => Tokens(h.Chunk.Text)).ToArray();
        var averageLength = Math.Max(1, docs.Average(d => d.Length));
        var idf = terms.ToDictionary(t => t, t =>
        {
            var frequency = docs.Count(d => d.Contains(t));
            return Math.Log(1 + (docs.Length - frequency + 0.5) / (frequency + 0.5));
        });
        double Bm25(string[] words, double average)
        {
            var frequencies = words.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
            return terms.Sum(t =>
            {
                var tf = frequencies.GetValueOrDefault(t);
                return idf[t] * tf * 2.2 / (tf + 1.2 * (0.25 + 0.75 * words.Length / average));
            });
        }
        return candidates.Select((hit, i) =>
        {
            var full = Bm25(docs[i], averageLength);
            var bestPassage = full;
            // Long chunks should not hide a short, directly relevant instruction.
            for (var start = 0; start < docs[i].Length; start += 96)
                bestPassage = Math.Max(bestPassage, Bm25(docs[i].Skip(start).Take(128).ToArray(), Math.Min(128, averageLength)));
            var lexical = 0.4 * full + 0.6 * bestPassage;
            var score = terms.Length == 0 ? hit.SimilarityScore
                : 0.4 * hit.SimilarityScore + 0.6 * lexical / (1 + lexical);
            return hit with { RerankScore = hit.FusionScore > 0 ? hit.FusionScore : score, RerankerMode = "lexical_fallback" };
        }).OrderByDescending(h => h.RerankScore).ThenBy(h => h.Chunk.ChunkId, StringComparer.Ordinal).ToArray();
    }
}
