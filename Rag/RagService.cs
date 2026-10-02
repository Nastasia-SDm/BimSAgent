namespace BimSAgentApp.Rag;

public sealed class CosineRetriever : IRetriever
{
    public static void ValidateVector(float[]? vector, int dimensions)
    {
        if (vector == null || vector.Length != dimensions || vector.Any(v => !float.IsFinite(v)) || vector.All(v => v == 0))
            throw new InvalidDataException("Неверная размерность или значения embedding.");
    }

    public static double Similarity(float[] left, float[] right)
    {
        ValidateVector(left, right.Length);
        ValidateVector(right, left.Length);
        double dot = 0, a = 0, b = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += (double)left[i] * right[i];
            a += (double)left[i] * left[i];
            b += (double)right[i] * right[i];
        }
        return Math.Clamp(dot / Math.Sqrt(a * b), -1, 1);
    }

    public IReadOnlyList<RetrievalHit> Search(RagIndex index, float[] query, int topK)
    {
        if (topK is < 1 or > 100) throw new ArgumentException("top-K должен быть от 1 до 100.");
        ValidateVector(query, index.Dimensions);
        return index.Chunks.Select(c => new RetrievalHit(c, Similarity(query, c.Embedding)))
            .OrderByDescending(h => h.SimilarityScore).ThenBy(h => h.Chunk.ChunkId, StringComparer.Ordinal).Take(topK).ToArray();
    }
}

public sealed class RagService(IEmbeddingClient embeddings, IRagAnswerGenerator generator,
    IIndexStore store, IRetriever retriever, RagTokenizer tokenizer)
{
    private const int ContextTokens = 6000;

    public async Task<RagAnswer> AskAsync(string question, string strategy = "fixed", bool noRag = false,
        int topK = 5, AnswerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateQuestion(question, topK);
        if (noRag)
            return new(await generator.GenerateAsync(new(question, [], true, options ?? new()), cancellationToken), [], true);
        RagDefaults.ValidateStrategy(strategy);
        var index = RequireIndex(strategy);
        var vector = await QueryEmbedding(question, cancellationToken);
        return await AnswerFromIndex(question, index, vector, topK, options ?? new(), cancellationToken);
    }

    public async Task<RagComparison> CompareAsync(string question, int topK = 5, AnswerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateQuestion(question, topK);
        var fixedIndex = RequireIndex("fixed");
        var structuralIndex = RequireIndex("structural");
        ValidatePair(fixedIndex, structuralIndex);
        var vector = await QueryEmbedding(question, cancellationToken);
        var settings = options ?? new();
        var first = await AnswerFromIndex(question, fixedIndex, vector, topK, settings, cancellationToken);
        var second = await AnswerFromIndex(question, structuralIndex, vector, topK, settings, cancellationToken);
        return new(first, second);
    }

    public static void ValidatePair(RagIndex first, RagIndex second)
    {
        if (first.BuildId != second.BuildId || first.Source != second.Source || first.ContentHash != second.ContentHash
            || first.EmbeddingModel != second.EmbeddingModel || first.Dimensions != second.Dimensions)
            throw new InvalidDataException("Индексы не образуют согласованную пару. Повторите rag index.");
    }

    private RagIndex RequireIndex(string strategy) => store.Load(strategy)
        ?? throw new InvalidOperationException($"Индекс {strategy} не найден. Сначала выполните rag index.");

    private void ValidateQuestion(string question, int topK)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Введите вопрос.");
        if (tokenizer.Count(question) > 8191) throw new ArgumentException("Вопрос превышает 8191 токен.");
        if (topK is < 1 or > 100) throw new ArgumentException("top-K должен быть от 1 до 100.");
    }

    private async Task<float[]> QueryEmbedding(string question, CancellationToken cancellationToken)
    {
        var values = await embeddings.EmbedAsync([question], cancellationToken);
        if (values.Length != 1) throw new InvalidDataException("Не получен embedding вопроса.");
        CosineRetriever.ValidateVector(values[0], RagDefaults.Dimensions);
        return values[0];
    }

    private async Task<RagAnswer> AnswerFromIndex(string question, RagIndex index, float[] vector, int topK,
        AnswerOptions options, CancellationToken cancellationToken)
    {
        // Extension point: future filters/reranker/threshold operate on hits, retaining original scores and IDs.
        var hits = retriever.Search(index, vector, topK);
        var selected = new List<RetrievalHit>();
        var budget = ContextTokens;
        foreach (var hit in hits)
        {
            var count = tokenizer.Count(hit.Chunk.EmbeddingInput) + tokenizer.Count(hit.Chunk.Source) + 100;
            if (count > budget) continue;
            selected.Add(hit);
            budget -= count;
        }
        if (selected.Count == 0) return new(RagDefaults.Unknown, [], false);
        var answer = await generator.GenerateAsync(new(question, selected, false, options), cancellationToken);
        return new(answer, selected, false);
    }
}
