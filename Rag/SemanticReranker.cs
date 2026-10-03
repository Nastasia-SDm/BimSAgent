using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BimSAgentApp.Rag;

// Endpoint contract: POST {query, texts, model}; response [{index, score}].
// The endpoint must run a cross-encoder, not a generative chat model.
public sealed class SemanticReranker(HttpClient http, Uri endpoint, string model, string? apiKey = null) : IReranker
{
    public IReadOnlyList<RetrievalHit> Rerank(string question, IReadOnlyList<RetrievalHit> candidates) =>
        throw new InvalidOperationException("Semantic reranker requires the asynchronous retrieval API.");

    public async Task<IReadOnlyList<RetrievalHit>> RerankAsync(string question, IReadOnlyList<RetrievalHit> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return [];
        var tokenizer = new RagTokenizer();
        var passages = candidates.SelectMany((hit, index) => tokenizer.Windows(hit.Chunk.EmbeddingInput, 350, 60)
            .Select(w => (Index: index, Text: hit.Chunk.EmbeddingInput[w.Start..w.End]))).ToArray();
        try
        {
            var scores = new double[passages.Length];
            for (var offset = 0; offset < passages.Length; offset += 32)
            {
                var batch = passages.Skip(offset).Take(32).ToArray();
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(JsonSerializer.Serialize(new { query = question, texts = batch.Select(p => p.Text), model, truncate = false }),
                    System.Text.Encoding.UTF8, "application/json");
                using var response = await http.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("Reranker HTTP " + (int)response.StatusCode, null, response.StatusCode);
                using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var values = result.RootElement.EnumerateArray().ToArray();
                var seen = new HashSet<int>();
                foreach (var item in values)
                {
                    var index = item.GetProperty("index").GetInt32(); var score = item.GetProperty("score").GetDouble();
                    if (index < 0 || index >= batch.Length || !seen.Add(index) || !double.IsFinite(score)) throw new InvalidDataException("Invalid reranker scores.");
                    scores[offset + index] = score;
                }
                if (seen.Count != batch.Length) throw new InvalidDataException("Incomplete reranker response.");
            }
            return candidates.Select((hit, index) =>
            {
                var best = passages.Select((p, i) => (Passage: p, Score: scores[i])).Where(p => p.Passage.Index == index)
                    .OrderByDescending(p => p.Score).First();
                return hit with { RerankScore = best.Score, MatchedPassage = best.Passage.Text, RerankerMode = "cross_encoder:" + model };
            }).OrderByDescending(h => h.RerankScore).ThenBy(h => h.Chunk.ChunkId, StringComparer.Ordinal).ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidDataException
            || e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var reason = e is HttpRequestException error && error.StatusCode is { } status ? "http_" + (int)status : e.GetType().Name;
            return new Bm25Reranker().Rerank(question, candidates).Select(h => h with { RerankerMode = "lexical_fallback:reranker_unavailable", SelectionReason = "reranker_" + reason }).ToArray();
        }
    }
}
