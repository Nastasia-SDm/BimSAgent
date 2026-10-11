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
            page.AddText("Ventilation removes polluted air.", 12, new PdfPoint(25, 700), font);
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
            check(index.EmbeddingModel == OllamaRagClient.EmbeddingModel && index.Dimensions == 768, "separate local embedding configuration persisted");
            var before = handler.Embeddings;
            check(await Command("rag", "index", pdf) == 0 && handler.Embeddings == before, "unchanged local document reuses disk index without model calls");
            check(File.ReadAllText(cloudSentinel) == "cloud sentinel", "local indexing never overwrites cloud index");
            check(await Command("rag", "ask", "What does ventilation do?") == 0 && output.ToString().Contains("polluted air"), "local retrieval and grounded generation");
            check(handler.Embeddings == before + 1 && handler.Generations == 1, "one query embedding plus one local answer");
            check(handler.QueryPrefix && handler.DocumentPrefix, "nomic document and query task prefixes");
            check(error.ToString().Contains("общее время запроса"), "local end-to-end time reported");
            check(await Command("rag", "ask", "What does ventilation do?", "--top-k", "100") == 0 && handler.LastFragmentCount <= 3, "local top-K bounded for weak laptop");
            var total = handler.Embeddings + handler.Generations;
            check(await Command("rag", "index", pdf, "--vision") == 1 && handler.Embeddings + handler.Generations == total, "local rejects cloud vision before any request");
            var plan = await client.PlanChatAsync(["standalone question"], "standalone question", new(new(), [], null), default);
            check(plan.Queries.SequenceEqual(new[] { "standalone question" }) && handler.Embeddings + handler.Generations == total, "local chat planning makes no additional model calls");
            var engine = new RagChatEngine(new RagService(client, client, store, new HybridRetriever(new Bm25Reranker()), new(), contextTokens: 1600), client,
                _ => JsonSerializer.SerializeToElement(new { }));
            var chatError = new StringWriter();
            check(await RagChatCommands.RunAsync(["rag", "chat", "--new"], default,
                new StringReader("What does ventilation do?\n/exit\n"), new StringWriter(), chatError, indexRoot, engine: engine) == 0,
                "local RAG chat works without OpenAI key or visual calls");
            check(chatError.ToString().Contains("общее время запроса"), "chat turn time reported");
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

    private sealed class NativeHttp(Action<bool, string> check) : HttpMessageHandler
    {
        public int Embeddings, Generations, LastFragmentCount;
        public bool QueryPrefix, DocumentPrefix, Fail, BadDimensions;
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
                && body.GetProperty("options").GetProperty("num_ctx").GetInt32() == 4096
                && body.GetProperty("options").GetProperty("num_predict").GetInt32() <= 500,
                "local generation uses bounded non-thinking qwen3");
            using var input = JsonDocument.Parse(body.GetProperty("messages")[1].GetProperty("content").GetString()!);
            LastFragmentCount = input.RootElement.GetProperty("questions")[0].GetProperty("fragments").GetArrayLength();
            var quote = input.RootElement.GetProperty("questions")[0].GetProperty("fragments")[0].GetProperty("text").GetString();
            return Json(new { done = true, done_reason = "stop", message = new { content = JsonSerializer.Serialize(new
                { answers = new[] { new { number = 1, text = "Ventilation removes polluted air.", status = "supported", evidence = new[] { new { quote } } } } }) } });
        }
        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
    }
}
