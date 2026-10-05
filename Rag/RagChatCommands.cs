using System.Text.Json;

namespace BimSAgentApp.Rag;

public static class RagChatCommands
{
    public const string Help = "rag chat [--new | --resume <id>] [--task <id>] [--strategy fixed|structural] [--baseline | --improved] [--top-k <1..100>] [--verbose]";
    public static async Task<int> RunAsync(string[] args, CancellationToken token, TextReader? input = null,
        TextWriter? output = null, TextWriter? error = null, string? dataDirectory = null, int? activeTaskId = null,
        RagChatEngine? engine = null, Func<int, JsonElement>? taskReader = null)
    {
        input ??= Console.In; output ??= Console.Out; error ??= Console.Error;
        taskReader ??= BimSAgent.ReadTaskContext;
        try
        {
            if (args.Length == 3 && args[2] == "--help") { await output.WriteLineAsync(Help); return 0; }
            var startNew = false; string? resume = null; var taskId = activeTaskId;
            var strategy = "structural"; var baseline = false; var improved = false; var verbose = false; int? topK = null;
            for (var i = 2; i < args.Length; i++)
                switch (args[i])
                {
                    case "--new": startNew = true; break;
                    case "--resume" when i + 1 < args.Length: resume = args[++i]; break;
                    case "--task" when i + 1 < args.Length: taskId = Positive(args[++i]); break;
                    case "--strategy" when i + 1 < args.Length: strategy = args[++i]; RagDefaults.ValidateStrategy(strategy); break;
                    case "--baseline": baseline = true; break;
                    case "--improved": improved = true; break;
                    case "--verbose": verbose = true; break;
                    case "--top-k" when i + 1 < args.Length:
                        topK = Positive(args[++i]); if (topK > 100) throw new ArgumentException("Top-K должен быть от 1 до 100."); break;
                    default: throw new ArgumentException(Help);
                }
            if (startNew && resume != null || baseline && improved) throw new ArgumentException("Несовместимые параметры. " + Help);
            var directory = dataDirectory ?? RagDefaults.DataDirectory;
            var store = new RagChatStore(Environment.GetEnvironmentVariable("BIMS_RAG_CHAT_DIRECTORY") ?? Path.Combine(directory, "chats"));
            using var lease = store.Lock();
            if (taskId is { } supplied) taskReader(supplied);
            var session = store.Open(startNew, resume, taskId);
            // Standalone CLI does not instantiate BimSAgent or touch its history/memory.
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
            var api = new OpenAiRagClient(http, embeddingOptions: EmbeddingOptions.FromEnvironment());
            using var rerankHttp = RagRetrievalFactory.CreateHttpClient();
            engine ??= new RagChatEngine(new RagService(api, api, new JsonIndexStore(directory),
                RagRetrievalFactory.Create(rerankHttp, baseline), new(),
                new ChatVisualContext(api, Path.Combine(directory, "visual-cache"), new())), api, taskReader);
            await output.WriteLineAsync($"RAG-чат {session.Id}; сообщений: {session.Turns.Length}; задача: {session.TaskId?.ToString() ?? "не привязана"}.");
            await output.WriteLineAsync("/new — новый; /resume <id> — продолжить; /state — состояние; /task <id>|none — привязка; /exit — вернуться.");
            while (!token.IsCancellationRequested)
            {
                await output.WriteAsync("rag> ");
                var line = await input.ReadLineAsync(token);
                if (line == null || line.Trim() is "/exit" or "/back") return 0;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var command = line.Trim();
                    if (command == "/state")
                    {
                        await output.WriteLineAsync(JsonSerializer.Serialize(new { session.Id, session.TaskId, session.State,
                            task = session.TaskId is { } id ? taskReader(id) : (JsonElement?)null }, JsonIndexStore.JsonOptions));
                        continue;
                    }
                    if (command == "/new") { session = store.Open(true, null, session.TaskId); await output.WriteLineAsync("Новый RAG-чат: " + session.Id); continue; }
                    if (command.StartsWith("/resume ", StringComparison.Ordinal))
                    { session = store.Open(false, command[8..].Trim(), null); await output.WriteLineAsync("Продолжен RAG-чат: " + session.Id); continue; }
                    if (command.StartsWith("/task ", StringComparison.Ordinal))
                    {
                        var value = command[6..].Trim(); var id = value == "none" ? (int?)null : Positive(value);
                        if (id is { } selected) taskReader(selected);
                        var updated = session with { TaskId = id }; store.Save(updated); session = updated;
                        await output.WriteLineAsync("Привязка задачи сохранена. Этап задачи не изменён."); continue;
                    }
                    if (command.StartsWith('/')) throw new ArgumentException("Команды: /new, /resume <id>, /state, /task <id>|none, /exit.");
                    var result = await engine.TurnAsync(session, line, strategy, topK, token);
                    store.Save(result.Session); session = result.Session;
                    if (verbose)
                    {
                        for (var i = 0; i < result.Queries.Length; i++) await output.WriteLineAsync($"Поисковый запрос {i + 1}: {result.Queries[i]}");
                        await RagCommands.WriteVerboseAsync(output, result.Answer);
                    }
                    await output.WriteLineAsync(result.Answer.Text);
                    await output.WriteLineAsync($"Top-K: {result.Answer.Sources.Count}");
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { await error.WriteLineAsync("Время ожидания истекло. Ход не сохранён; повторите вопрос."); }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or HttpRequestException)
                { await error.WriteLineAsync("RAG-чат: " + e.Message); }
            }
            return 0;
        }
        catch (OperationCanceledException) { return 1; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        { await error.WriteLineAsync("RAG-чат: " + e.Message); return 1; }
    }
    private static int Positive(string text) => int.TryParse(text, out var id) && id > 0 ? id : throw new ArgumentException("Ожидается положительное число.");
}

internal static class RagRetrievalFactory
{
    public static HttpClient CreateHttpClient()
    {
        var url = Environment.GetEnvironmentVariable("BIMS_RERANK_URL");
        return new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback) })
        { Timeout = TimeSpan.FromMinutes(5) };
    }
    public static IRetriever Create(HttpClient http, bool baseline)
    {
        if (baseline) return new CosineRetriever();
        var url = Environment.GetEnvironmentVariable("BIMS_RERANK_URL");
        IReranker reranker = string.IsNullOrWhiteSpace(url) ? new Bm25Reranker()
            : new SemanticReranker(http, new Uri(url), Environment.GetEnvironmentVariable("BIMS_RERANK_MODEL") ?? "BAAI/bge-reranker-v2-m3",
                Environment.GetEnvironmentVariable("BIMS_RERANK_API_KEY"));
        return new HybridRetriever(reranker);
    }
}
