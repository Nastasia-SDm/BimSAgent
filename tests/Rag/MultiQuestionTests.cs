using System.Net;
using System.Text.Json;
using BimSAgentApp.Rag;

static class MultiQuestionTests
{
    public static async Task Run(Action<bool, string> check, IIndexStore store, RagTokenizer tokenizer)
    {
        const string list = "1. Про площадку? 2. Про балку? 3. Про арматуру?";
        var expected = new[] { "Про площадку?", "Про балку?", "Про арматуру?" };
        check(QuestionParser.Split(list).SequenceEqual(expected), "numbered questions parsed in C#");
        check(QuestionParser.Split(" 1. Про площадку?\r\n2. Про балку?\n3. Про арматуру? ").SequenceEqual(expected), "multiline numbered questions");
        foreach (var single in new[] { "Обычный вопрос?", "Revit 2024. Размер 1.25?", "Пункт 1. Что это? 2. Далее?", "1. Единственный?", "1. Первый? 3. Пропуск?", "1. 2. Пустой?" })
            check(QuestionParser.Split(single).SequenceEqual(new[] { single }), "single or non-list text preserved: " + single);

        var embeddings = new Embeddings();
        var retrieval = new Retriever();
        var generation = new Generator();
        var service = new RagService(embeddings, generation, store, retrieval, tokenizer);
        var result = await service.AskAsync(list, "structural", topK: 2);
        check(embeddings.Inputs.SequenceEqual(expected), "one independent embedding input per question");
        check(retrieval.Queries.Count == 3 && retrieval.Queries.Select(v => v[0]).SequenceEqual(new float[] { 1, 2, 3 })
            && retrieval.TopKs.All(k => k == 2), "independent retrieval and top-K per embedding");
        check(generation.Requests.Count == 1 && generation.Requests[0].Questions!.Select(q => q.Question).SequenceEqual(expected), "one LLM call for all questions");
        var contexts = generation.Requests[0].Questions!;
        check(contexts.Select(c => c.Context.Single().SimilarityScore).SequenceEqual(new double[] { 1, 2, 3 }), "each question retains its own hits");
        check(result.Text == "⮕1. Ответ 1\n⮕2. Ответ 2\n⮕3. Ответ 3", "numbered answers passed through");
        check(result.RetrievedQuestions.Select(q => q.Question).SequenceEqual(expected)
            && result.RetrievedQuestions.Select(q => q.Context.Single().SimilarityScore).SequenceEqual(new double[] { 1, 2, 3 }),
            "diagnostics retain per-question retrieval results");
        var oversized = await new RagService(embeddings, generation, store, new OverBudgetRetriever(), tokenizer)
            .AskAsync("Большой фрагмент", "structural");
        check(oversized.Sources.Count == 0 && oversized.RetrievedQuestions.Single().Context.Count == 1,
            "diagnostics retain top-K omitted from generation by context budget");

        var noRag = new RagService(embeddings, generation, new UnavailableStore(), retrieval, tokenizer);
        await noRag.AskAsync(list, noRag: true);
        check(embeddings.Inputs.Count == 4 && retrieval.Queries.Count == 3 && generation.Requests.Count == 2,
            "NO-RAG one LLM call and no index, embeddings or retrieval");
        check(generation.Requests[^1].Questions!.All(q => q.Context.Count == 0), "NO-RAG all question contexts empty");
        await service.AskAsync("Один вопрос", "structural");
        check(embeddings.Inputs[^1] == "Один вопрос" && generation.Requests[^1].Questions == null, "single question retains old request shape");

        var empty = new RagService(embeddings, generation, store, new EmptyRetriever(), tokenizer);
        var count = generation.Requests.Count;
        await empty.AskAsync(list, "structural");
        check(generation.Requests.Count == count + 1 && generation.Requests[^1].Questions!.All(q => q.Context.Count == 0), "empty multi-question contexts still use one shared generation");

        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var api = new OpenAiRagClient(http, () => "test-key-no-network");
        await api.GenerateAsync(new(list, [], false, new(), contexts), default);
        await api.GenerateAsync(new(list, [], true, new(), contexts.Select(c => c with { Context = [] }).ToArray()), default);
        check(handler.Payloads.Count == 2, "one Responses HTTP request per multi-question generation");
        using var ragInput = JsonDocument.Parse(handler.Payloads[0].GetProperty("input").GetString()!);
        using var noRagInput = JsonDocument.Parse(handler.Payloads[1].GetProperty("input").GetString()!);
        check(ragInput.RootElement.GetProperty("questions").EnumerateArray().Select(q => q.GetProperty("question").GetString()).SequenceEqual(expected)
            && ragInput.RootElement.GetProperty("questions").EnumerateArray().All(q => q.GetProperty("fragments").GetArrayLength() == 1), "HTTP prompt groups questions with their fragments");
        check(noRagInput.RootElement.GetProperty("questions").EnumerateArray().All(q => q.GetProperty("fragments").GetArrayLength() == 0), "HTTP NO-RAG has no context");
        check(handler.Payloads.All(p => p.GetProperty("instructions").GetString()!.Contains("⮕1.")), "HTTP prompt specifies numbered output");
    }

    sealed class Embeddings : IEmbeddingClient
    {
        public List<string> Inputs { get; } = [];
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            if (texts.Count != 1) throw new Exception("Expected one question per embedding call.");
            Inputs.Add(texts[0]);
            var vector = new float[1536]; vector[0] = Inputs.Count;
            return Task.FromResult(new[] { vector });
        }
    }
    sealed class Retriever : IRetriever
    {
        public List<float[]> Queries { get; } = [];
        public List<int> TopKs { get; } = [];
        public IReadOnlyList<RetrievalHit> Search(RagIndex index, float[] query, int topK)
        {
            Queries.Add(query); TopKs.Add(topK);
            return [new(index.Chunks[0], query[0])];
        }
    }
    sealed class Generator : IRagAnswerGenerator
    {
        public List<RagAnswerRequest> Requests { get; } = [];
        public Task<string> GenerateAsync(RagAnswerRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(request.Questions == null ? "Один ответ" :
                string.Join("\n", request.Questions.Select((q, i) => $"⮕{i + 1}. Ответ {i + 1}")));
        }
    }
    sealed class UnavailableStore : IIndexStore
    {
        public RagIndex? Load(string strategy) => throw new Exception("NO-RAG touched index.");
        public Task SavePairAsync(RagIndex fixedIndex, RagIndex structuralIndex, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    sealed class OverBudgetRetriever : IRetriever
    {
        public IReadOnlyList<RetrievalHit> Search(RagIndex index, float[] query, int topK) =>
            [new(index.Chunks[0] with { Text = string.Concat(Enumerable.Repeat("много текста ", 10000)) }, 0.9)];
    }
    sealed class Handler : HttpMessageHandler
    {
        public List<JsonElement> Payloads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Payloads.Add(payload.RootElement.Clone());
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                status = "completed", output = new[] { new { content = new[] { new { type = "output_text", text = "⮕1. Ответ 1\n⮕2. Ответ 2\n⮕3. Ответ 3" } } } }
            })) };
        }
    }
}
