using System.Net;
using System.Text.Json;
using BimSAgentApp.Rag;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

static class LocalRagTests
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        var saved = new[] { "BIMS_LLM_PROVIDER", "OPENAI_API_KEY", "BIMS_ASSET_URL", "BIMS_RERANK_URL", "BIMS_EMBEDDING_MODEL" }
            .ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("BIMS_LLM_PROVIDER", "ollama");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("BIMS_ASSET_URL", "https://must-not-be-contacted.invalid");
            Environment.SetEnvironmentVariable("BIMS_RERANK_URL", "https://must-not-be-contacted.invalid");
            Environment.SetEnvironmentVariable("BIMS_EMBEDDING_MODEL", "unused-cloud-model");
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            var page = builder.AddPage(PageSize.A4);
            page.AddText("Ventilation removes polluted air. Pressure is 12.5 Pa.", 12, new PdfPoint(25, 700), font);
            var pdf = Path.Combine(root, "local fixture.pdf");
            File.WriteAllBytes(pdf, builder.Build());
            var document = new DocxDocumentExtractor().Extract(pdf);
            check(document.Blocks.Single().Text.Contains("Ventilation") && document.Blocks[0].Section == "Страница 1", "local PDF extraction and page provenance");
            var indexRoot = Path.Combine(root, "provider-isolation");
            Directory.CreateDirectory(indexRoot);
            var cloudSentinel = Path.Combine(indexRoot, "fixed.json");
            File.WriteAllText(cloudSentinel, "cloud sentinel");
            var handler = new NativeHttp(check);
            using var http = new HttpClient(handler);
            var client = new OllamaRagClient(http);
            var output = new StringWriter(); var error = new StringWriter();
            Task<int> Command(params string[] args) => RagCommands.RunAsync(args, default, output, error, indexRoot, client, client);
            check(await Command("rag", "index", pdf) == 0, "local PDF index without OpenAI key: " + error);
            var directory = RagProvider.DirectoryFor(indexRoot, true);
            var store = new JsonIndexStore(directory);
            var index = store.Load("fixed")!;
            await RetrievalBudgetTests(check, index);
            check(index.EmbeddingModel == OllamaRagClient.EmbeddingModel && index.Dimensions == 768, "separate local embedding configuration persisted");
            var before = handler.Embeddings;
            check(await Command("rag", "index", pdf) == 0 && handler.Embeddings == before, "unchanged local document reuses disk index without model calls");
            check(File.ReadAllText(cloudSentinel) == "cloud sentinel", "local indexing never overwrites cloud index");
            check(await Command("rag", "ask", "What does ventilation do?") == 0 && output.ToString().Contains("polluted air"), "local retrieval and grounded generation");
            check(handler.Embeddings == before + 1 && handler.Generations == 1, "one query embedding plus one local answer");
            check(handler.LastFragmentText.Contains("12.5 Pa") && handler.LastFragmentText.Contains("[1]"),
                "evidence IDs preserve literal document text and decimal numbers");
            check(handler.QueryPrefix && handler.DocumentPrefix, "nomic document and query task prefixes");
            var cacheRoot = Path.Combine(root, "query-vector-cache");
            var cachedClient = new OllamaRagClient(http, cacheRoot);
            var cacheCalls = handler.Embeddings;
            var cachedVector = await cachedClient.EmbedQueryAsync("cache question", default);
            var reusedVector = await new OllamaRagClient(http, cacheRoot).EmbedQueryAsync("cache question", default);
            check(handler.Embeddings == cacheCalls + 1 && cachedVector[0].SequenceEqual(reusedVector[0]),
                "query embedding cache survives new client and avoids another model call");
            await cachedClient.EmbedQueryAsync("different cache question", default);
            check(handler.Embeddings == cacheCalls + 2, "different question computes its own embedding");
            var cachedPath = Path.Combine(cacheRoot, RagDefaults.Hash(OllamaRagClient.EmbeddingModel + "|768|search_query: cache question") + ".json");
            File.WriteAllText(cachedPath, "[0]");
            await cachedClient.EmbedQueryAsync("cache question", default);
            check(handler.Embeddings == cacheCalls + 3, "corrupt query cache safely recomputes one vector");
            var changedIndex = index with { Chunks = [index.Chunks[0] with { Text = "Changed document: fresh ventilation instructions." }] };
            var freshRetrieval = new RagService(cachedClient, cachedClient, new RetrievalFixtureStore(changedIndex),
                new HybridRetriever(new Bm25Reranker()), new(), contextTokens: 1000, compactContext: true);
            var fresh = await freshRetrieval.RetrieveOnlyAsync("cache question", "fixed", 2, default);
            check(handler.Embeddings == cacheCalls + 3 && fresh.Sources.Single().Chunk.Text.StartsWith("Changed document"),
                "query cache never reuses old retrieval or document context");
            check(error.ToString().Contains("общее время запроса"), "local end-to-end time reported");
            check(await Command("rag", "ask", "What does ventilation do?", "--top-k", "100") == 0 && handler.LastFragmentCount <= 3, "local top-K bounded for weak laptop");
            var total = handler.Embeddings + handler.Generations;
            check(await Command("rag", "index", pdf, "--vision") == 1 && handler.Embeddings + handler.Generations == total, "local rejects cloud vision before any request");
            var plan = await client.PlanChatAsync(["standalone question"], "standalone question", new(new(), [], null), default);
            check(plan.Queries.SequenceEqual(new[] { "standalone question" }) && handler.Embeddings + handler.Generations == total, "local chat planning makes no additional model calls");
            var engine = new RagChatEngine(new RagService(client, client, store, new HybridRetriever(new Bm25Reranker()), new(), contextTokens: 1000, compactContext: true), client,
                _ => JsonSerializer.SerializeToElement(new { }));
            var chatError = new StringWriter();
            check(await RagChatCommands.RunAsync(["rag", "chat", "--new"], default,
                new StringReader("What does ventilation do?\n/exit\n"), new StringWriter(), chatError, indexRoot, engine: engine) == 0,
                "local RAG chat works without OpenAI key or visual calls");
            check(chatError.ToString().Contains("общее время запроса"), "chat turn time reported");
            handler.InvalidEvidence = true;
            var generationsBeforeInvalidEvidence = handler.Generations;
            check(await Command("rag", "ask", "What does ventilation do?") == 1 && handler.Generations == generationsBeforeInvalidEvidence + 1,
                "invalid evidence ID rejected without another model call");
            handler.InvalidEvidence = false;
            handler.Fail = true;
            check(await Command("rag", "ask", "failure") == 1, "Ollama failure returns error without cloud fallback");
            handler.Fail = false;
            handler.BadDimensions = true;
            try { await client.EmbedAsync(["text"], default); throw new Exception("bad vector accepted"); }
            catch (InvalidDataException) { check(true, "local rejects malformed vector dimensions"); }

            Environment.SetEnvironmentVariable("BIMS_LLM_PROVIDER", "openai");
            Environment.SetEnvironmentVariable("BIMS_EMBEDDING_MODEL", null);
            check(RagProvider.DirectoryFor(indexRoot, false) == indexRoot, "cloud index location unchanged");
            var cloudError = new StringWriter();
            check(await RagCommands.RunAsync(["rag", "ask", "cloud", "--no-rag"], default, new StringWriter(), cloudError, indexRoot) == 1
                && cloudError.ToString().Contains("OPENAI_API_KEY") && cloudError.ToString().Contains("общее время запроса"),
                "cloud still requires OpenAI key and reports elapsed time on error");
        }
        finally { foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    }

    private static async Task RetrievalBudgetTests(Action<bool, string> check, RagIndex fixture)
    {
        var tokenizer = new RagTokenizer();
        var assembler = new ContextAssembler(tokenizer);
        const string fact = "Аэродинамический расчет вентиляции определяет потери давления в воздуховодах.";
        var original = fixture.Chunks[0] with
        {
            Text = string.Concat(Enumerable.Repeat("Общее описание системы без конкретных рекомендаций. ", 180)) + fact,
            Section = "Расчет аэродинамики", FamilyPurpose = new string('x', 6000),
            ChunkId = "large-relevant", OwnerFamilyId = "ventilation"
        };
        var index = fixture with { Chunks = [original] };
        var selection = new HybridRetriever(new Bm25Reranker()).Retrieve(index, original.Embedding,
            "Что определяет аэродинамический расчет вентиляции?", 2);
        check(selection.Candidates.Single().Selected, "relevant long chunk passes retrieval relevance filters");
        check(!assembler.Assemble(selection, 1000, index).Candidates.Any(h => h.Selected),
            "regression reproduces zero chunks under cloud fragment budget accounting");
        var local = assembler.AssembleLocal(selection, 1000, "Что определяет аэродинамический расчет вентиляции?", 2);
        var hit = local.Candidates.Single(h => h.Selected);
        check(hit.Chunk.Text.Contains(fact) && hit.SelectionReason == "selected_excerpt", "local chooses relevant passage near end of oversized chunk");
        check(tokenizer.Count(JsonSerializer.Serialize(ContextAssembler.LocalFragment(hit), JsonIndexStore.JsonOptions)) + 4 <= 1000,
            "actual local fragment stays within unchanged token budget");
        check(original.Text.Length > hit.Chunk.Text.Length && index.Chunks[0].Text == original.Text
            && hit.Chunk.ChunkId == original.ChunkId && hit.Chunk.Source == original.Source,
            "excerpt preserves provenance and leaves persisted source intact");
        var model = new RetrievalFixtureClient(original.Embedding);
        var service = new RagService(model, model, new RetrievalFixtureStore(index), new HybridRetriever(new Bm25Reranker()), tokenizer,
            contextTokens: 1000, compactContext: true);
        var retrieved = await service.RetrieveOnlyAsync("Что определяет аэродинамический расчет вентиляции?", "fixed", 2, default);
        check(retrieved.Sources.Count == 1 && retrieved.Sources[0].Chunk.Text.Contains(fact),
            "local RagService passes relevant oversized chunk through retrieval and compact assembly");
        var tooExpensive = new RetrievalHit(original with { ChunkId = "expensive-section", Section = new string('界', 240) }, 1)
            { Selected = true };
        var reserve = new RetrievalHit(original with { ChunkId = "reserve", Text = fact, Section = "Расчет" }, 1)
            { Selected = false, SelectionReason = "family_limit" };
        var blocked = new[] { "source_conflict", "threshold", "duplicate", "different_owner" }.Select((reason, i) => reserve with
            { Chunk = reserve.Chunk with { ChunkId = "blocked-" + i }, SelectionReason = reason }).ToArray();
        var refill = assembler.AssembleLocal(new(new[] { tooExpensive, reserve }.Concat(blocked).ToArray(), 6), 180, "расчет вентиляции", 1);
        check(refill.Candidates.Single(h => h.Selected).Chunk.ChunkId == "reserve", "eligible reserve fills slot vacated by token-budget rejection");
        check(refill.Candidates.Where(h => h.Chunk.ChunkId.StartsWith("blocked-")).All(h => !h.Selected),
            "local refill keeps conflict, threshold, duplicate and owner filters");
        var sameFamily = Enumerable.Range(0, 5).Select(i => reserve with { Chunk = reserve.Chunk with { ChunkId = "same-" + i } }).ToArray();
        check(assembler.AssembleLocal(new(sameFamily, 5), 1000, "расчет", 3).Candidates.Count(h => h.Selected) == 2,
            "local assembly preserves two passages per family and bounded top-K");
    }

    private sealed class RetrievalFixtureClient(float[] vector) : IEmbeddingClient, IRagAnswerGenerator
    {
        public EmbeddingOptions Options => new(OllamaRagClient.EmbeddingModel, vector.Length);
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken token) => Task.FromResult(new[] { vector });
        public Task<string> GenerateAsync(RagAnswerRequest request, CancellationToken token) => throw new Exception("Retrieval must not generate answers.");
    }

    private sealed class RetrievalFixtureStore(RagIndex index) : IIndexStore
    {
        public RagIndex? Load(string strategy) => index;
        public Task SavePairAsync(RagIndex fixedIndex, RagIndex structuralIndex, CancellationToken token) => throw new Exception("Retrieval must not rewrite the index.");
    }

    private sealed class NativeHttp(Action<bool, string> check) : HttpMessageHandler
    {
        public int Embeddings, Generations, LastFragmentCount;
        public bool QueryPrefix, DocumentPrefix, Fail, BadDimensions, InvalidEvidence;
        public string LastFragmentText = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            check(request.RequestUri!.Host == "127.0.0.1" && request.RequestUri.Port == 11434 && request.Headers.Authorization == null,
                "RAG model request stays on Ollama loopback without cloud credentials");
            if (Fail) return new(HttpStatusCode.ServiceUnavailable);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var body = json.RootElement;
            if (request.RequestUri.AbsolutePath == "/api/embed")
            {
                Embeddings++;
                var inputs = body.GetProperty("input").EnumerateArray().Select(x => x.GetString()!).ToArray();
                DocumentPrefix |= inputs.Any(x => x.StartsWith("search_document: "));
                QueryPrefix |= inputs.Any(x => x.StartsWith("search_query: "));
                check(inputs.Length <= 2 && !body.GetProperty("truncate").GetBoolean(), "small embedding batches with no silent truncation");
                var vector = new float[BadDimensions ? 3 : 768]; vector[0] = 1;
                return Json(new { embeddings = inputs.Select(_ => vector) });
            }
            check(request.RequestUri.AbsolutePath == "/api/chat", "local generation uses native chat API");
            Generations++;
            check(body.GetProperty("model").GetString() == "qwen3:8b" && !body.GetProperty("think").GetBoolean()
                && body.GetProperty("options").GetProperty("num_ctx").GetInt32() == 3072
                && body.GetProperty("options").GetProperty("temperature").GetDouble() <= 0.2
                && body.GetProperty("options").GetProperty("num_predict").GetInt32() <= 224
                && body.GetProperty("options").GetProperty("num_batch").GetInt32() == 128
                && body.GetProperty("options").GetProperty("num_thread").GetInt32() <= 4,
                "local generation uses bounded non-thinking qwen3");
            using var input = JsonDocument.Parse(body.GetProperty("messages")[1].GetProperty("content").GetString()!);
            LastFragmentCount = input.RootElement.GetProperty("questions")[0].GetProperty("fragments").GetArrayLength();
            LastFragmentText = input.RootElement.GetProperty("questions")[0].GetProperty("fragments")[0].GetProperty("text").GetString()!;
            var evidenceSchema = body.GetProperty("format").GetProperty("properties").GetProperty("answers").GetProperty("items")
                .GetProperty("properties").GetProperty("evidence");
            check(evidenceSchema.GetProperty("maxItems").GetInt32() == 2 && evidenceSchema.GetProperty("items").GetProperty("type").GetString() == "integer",
                "model selects compact source IDs instead of copying quotations");
            return Json(new { done = true, done_reason = "stop", message = new { content = JsonSerializer.Serialize(new
                { answers = new[] { new { number = 1, text = "Ventilation removes polluted air.", status = "supported", evidence = new[] { InvalidEvidence ? 99999 : 1 } } } }) } });
        }
        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
    }
}
