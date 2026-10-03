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
        int? topK = null, AnswerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateQuestion(question, topK);
        var questions = QuestionParser.Split(question);
        if (questions.Count > 1)
        {
            foreach (var item in questions) ValidateQuestion(item, topK);
            var contexts = new List<RagQuestionContext>();
            var retrievedQuestions = new List<RagQuestionContext>();
            var multiIndex = noRag ? null : RequireIndex(strategy);
            foreach (var item in questions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<RetrievalHit> hits = [];
                var scanned = 0;
                IReadOnlyList<RetrievalHit> context = [];
                if (!noRag)
                {
                    var selection = await SelectContext(multiIndex!, await QueryEmbedding(item, cancellationToken), item, topK, cancellationToken);
                    hits = selection.Candidates; scanned = selection.ScannedCount;
                    context = hits.Where(h => h.Selected).ToArray();
                }
                contexts.Add(new(item, context));
                retrievedQuestions.Add(new(item, hits) { ScannedCount = scanned });
            }
            // Keep question/context associations in the request; never embed the combined list.
            var answer = await generator.GenerateAsync(new(question, [], noRag, options ?? new(), contexts), cancellationToken);
            return new(answer, contexts.SelectMany(c => c.Context).ToArray(), noRag) { RetrievedQuestions = retrievedQuestions };
        }
        if (noRag)
            return new(await generator.GenerateAsync(new(question, [], true, options ?? new()), cancellationToken), [], true);
        RagDefaults.ValidateStrategy(strategy);
        var index = RequireIndex(strategy);
        var vector = await QueryEmbedding(question, cancellationToken);
        return await AnswerFromIndex(question, index, vector, topK, options ?? new(), cancellationToken);
    }

    public async Task<RagComparison> CompareAsync(string question, int? topK = null, AnswerOptions? options = null,
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

    private RagIndex RequireIndex(string strategy)
    {
        var index = store.Load(strategy) ?? throw new InvalidOperationException($"Индекс {strategy} не найден. Сначала выполните rag index.");
        if (strategy == "structural" && index.ChunkSize != 2000)
            throw new InvalidOperationException("Structural-индекс создан с прежним размером чанка. Выполните rag index для размера 2000 токенов.");
        if (index.EmbeddingModel != embeddings.Options.Model || index.Dimensions != embeddings.Options.Dimensions)
            throw new InvalidOperationException("Embedding-конфигурация не совпадает с индексом. Восстановите прежние настройки или выполните rag index.");
        return index;
    }

    private void ValidateQuestion(string question, int? topK)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Введите вопрос.");
        if (tokenizer.Count(question) > 8191) throw new ArgumentException("Вопрос превышает 8191 токен.");
        if (topK is < 1 or > 100) throw new ArgumentException("top-K должен быть от 1 до 100.");
    }

    private async Task<float[]> QueryEmbedding(string question, CancellationToken cancellationToken)
    {
        var values = await embeddings.EmbedAsync([question], cancellationToken);
        if (values.Length != 1) throw new InvalidDataException("Не получен embedding вопроса.");
        CosineRetriever.ValidateVector(values[0], embeddings.Options.Dimensions);
        return values[0];
    }

    private async Task<RagAnswer> AnswerFromIndex(string question, RagIndex index, float[] vector, int? topK,
        AnswerOptions options, CancellationToken cancellationToken)
    {
        var selection = await SelectContext(index, vector, question, topK, cancellationToken);
        var hits = selection.Candidates; var scanned = selection.ScannedCount;
        IReadOnlyList<RetrievalHit> selected = hits.Where(h => h.Selected).ToArray();
        if (selected.Count == 0) return new(RagDefaults.Unknown, [], false) { RetrievedQuestions = [new(question, hits) { ScannedCount = scanned }] };
        var answer = await generator.GenerateAsync(new(question, selected, false, options), cancellationToken);
        return new(answer, selected, false) { RetrievedQuestions = [new(question, hits) { ScannedCount = scanned }] };
    }

    public async Task<RagAnswer> RetrieveOnlyAsync(string question, string strategy, int? topK, CancellationToken cancellationToken)
    {
        ValidateQuestion(question, topK);
        var index = RequireIndex(strategy);
        var groups = new List<RagQuestionContext>();
        foreach (var item in QuestionParser.Split(question))
        {
            var selection = await SelectContext(index, await QueryEmbedding(item, cancellationToken), item, topK, cancellationToken);
            groups.Add(new(item, selection.Candidates) { ScannedCount = selection.ScannedCount });
        }
        return new("Retrieval-only: генерация ответа не выполнялась.", groups.SelectMany(g => g.Context).Where(h => h.Selected).ToArray(), false)
        { RetrievedQuestions = groups };
    }

    private async Task<RetrievalSelection> SelectContext(RagIndex index, float[] vector, string question, int? topK, CancellationToken cancellationToken)
        => new ContextAssembler(tokenizer).Assemble(await retriever.RetrieveAsync(index, vector, question, topK, cancellationToken), ContextTokens, index);
}
