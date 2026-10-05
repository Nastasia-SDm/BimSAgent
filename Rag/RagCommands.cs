using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BimSAgentApp.Rag;

public static class RagCommands
{
   public const string Help = "rag index \"<docx>\" [--vision [--family <точное имя>]] | rag status | rag compare \"<вопрос>\" | rag ask \"<вопрос>\" [--strategy fixed|structural | --no-rag] [--baseline | --improved] [--top-k <максимум>] [--retrieval-only] [--json | --verbose] | " + RagChatCommands.Help;

    public static string[] Split(string line)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var quoted = false;
        var started = false;
        foreach (var character in line)
        {
            if (character == '"') { quoted = !quoted; started = true; }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (started) { words.Add(word.ToString()); word.Clear(); started = false; }
            }
            else { word.Append(character); started = true; }
        }
        if (quoted) throw new ArgumentException("Не закрыта кавычка в команде.");
        if (started) words.Add(word.ToString());
        return words.ToArray();
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken,
        TextWriter? output = null, TextWriter? error = null, string? dataDirectory = null,
        IEmbeddingClient? embeddingClient = null, IRagAnswerGenerator? answerGenerator = null, int? activeTaskId = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        try
        {
            if (args.Length < 2 || !args[0].Equals("rag", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(Help);
            var command = args[1].ToLowerInvariant();
            if (command == "chat")
                return await RagChatCommands.RunAsync(args, cancellationToken, output: output, error: error,
                    dataDirectory: dataDirectory, activeTaskId: activeTaskId);
            if (command is "help" or "--help") { await output.WriteLineAsync(Help); return 0; }
            var store = new JsonIndexStore(dataDirectory ?? RagDefaults.DataDirectory);
            if (command == "status")
            {
                if (args.Length != 2) throw new ArgumentException(Help);
                var first = store.Load("fixed");
                var second = store.Load("structural");
                if (first != null && second != null) RagService.ValidatePair(first, second);
                foreach (var strategy in new[] { "fixed", "structural" })
                {
                    var index = strategy == "fixed" ? first : second;
                    await output.WriteLineAsync(index == null ? $"{strategy}: индекс не создан." :
                        $"{strategy}: {index.Chunks.Length} чанков; {index.EmbeddingModel}; {index.CreatedAt:u}; источник: {index.Source}");
                }
                return 0;
            }
            if (command is not ("index" or "ask" or "compare") || args.Length < 3 || string.IsNullOrWhiteSpace(args[2]))
                throw new ArgumentException(Help);
            var strategyOption = "fixed";
            var strategySpecified = false;
            var noRag = false;
            var json = false;
            var verbose = false;
            var retrievalOnly = false;
            var baseline = false;
            var improved = false;
            var vision = false;
            string? visionFamily = null;
            int? topK = null;
            for (var i = 3; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--vision" when command == "index": vision = true; break;
                    case "--family" when command == "index" && i + 1 < args.Length: visionFamily = args[++i]; break;
                    case "--strategy" when command == "ask" && i + 1 < args.Length:
                        strategyOption = args[++i]; strategySpecified = true; RagDefaults.ValidateStrategy(strategyOption); break;
                    case "--no-rag" when command == "ask": noRag = true; break;
                    case "--top-k" when command != "index" && i + 1 < args.Length:
                        if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedTopK) || parsedTopK is < 1 or > 100)
                            throw new ArgumentException("top-K должен быть от 1 до 100.");
                        topK = parsedTopK;
                        break;
                    case "--json" when command != "index": json = true; break;
                    case "--verbose" when command == "ask": verbose = true; break;
                    case "--retrieval-only" when command == "ask": retrievalOnly = true; break;
                    case "--baseline" when command == "ask": baseline = true; break;
                    case "--improved" when command == "ask": improved = true; break;
                    default: throw new ArgumentException("Неизвестный или неполный параметр: " + args[i]);
                }
            }
            if (noRag && strategySpecified) throw new ArgumentException("--no-rag не совмещается с --strategy.");
            if (noRag && retrievalOnly) throw new ArgumentException("--retrieval-only не совмещается с --no-rag.");
            if (verbose && json) throw new ArgumentException("--verbose не совмещается с --json.");
            if (baseline && improved)
                throw new ArgumentException("Выберите только один режим: --baseline или --improved.");

            if (noRag && (baseline || improved))
                throw new ArgumentException("--baseline/--improved не совмещаются с --no-rag.");
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
            var api = new OpenAiRagClient(http, embeddingOptions: EmbeddingOptions.FromEnvironment());
            var embeddings = embeddingClient ?? api;
            var tokenizer = new RagTokenizer();
            if (command == "index")
            {
                if (visionFamily != null && !vision) throw new ArgumentException("--family требует --vision.");
                IDocumentAssetProcessor? processor = null;
                var assetUrl = Environment.GetEnvironmentVariable("BIMS_ASSET_URL");
                using var assetHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false,
                    UseProxy = !(Uri.TryCreate(assetUrl, UriKind.Absolute, out var assetUri) && assetUri.IsLoopback) }) { Timeout = TimeSpan.FromMinutes(5) };
                if (!string.IsNullOrWhiteSpace(assetUrl)) processor = new HttpDocumentAssetProcessor(assetHttp, new Uri(assetUrl),
                    Environment.GetEnvironmentVariable("BIMS_ASSET_VERSION") ?? throw new ArgumentException("Задайте BIMS_ASSET_VERSION."),
                    Environment.GetEnvironmentVariable("BIMS_ASSET_API_KEY"));
                var indexing = new IndexingService(new(), tokenizer, embeddings, store, processor, vision ? api : null, visionFamily);
                var counts = await indexing.IndexAsync(args[2], cancellationToken);
                await output.WriteLineAsync($"{(counts.Unchanged ? "Документ не изменился" : "Индексация завершена")}: fixed={counts.Fixed}, structural={counts.Structural}.");
                return 0;
            }
            using var rerankHttp = RagRetrievalFactory.CreateHttpClient();
            IRetriever retriever = RagRetrievalFactory.Create(rerankHttp, baseline);

            var service = new RagService(
                embeddings,
                answerGenerator ?? api,
                store,
                retriever,
                tokenizer);
            if (command == "compare")
            {
                var result = await service.CompareAsync(args[2], topK, cancellationToken: cancellationToken);
                if (json) await output.WriteLineAsync(JsonSerializer.Serialize(new { fixed_result = Diagnostic(result.Fixed), structural_result = Diagnostic(result.Structural) }, JsonIndexStore.JsonOptions));
                else
                {
                    await output.WriteLineAsync("Фиксированное разбиение:");
                    await output.WriteLineAsync(result.Fixed.Text);
                    await output.WriteLineAsync();
                    await output.WriteLineAsync("Структурное разбиение:");
                    await output.WriteLineAsync(result.Structural.Text);
                }
            }
            else
            {
                var result = retrievalOnly ? await service.RetrieveOnlyAsync(args[2], strategyOption, topK, cancellationToken)
                    : await service.AskAsync(args[2], strategyOption, noRag, topK, cancellationToken: cancellationToken);
                if (verbose) await WriteVerboseAsync(output, result);
                if (!json)
                    await output.WriteLineAsync();

                await output.WriteLineAsync(json
                    ? JsonSerializer.Serialize(Diagnostic(result), JsonIndexStore.JsonOptions)
                    : result.Text);
                if (!json && !noRag)
                    await output.WriteLineAsync($"Top-K: {result.Sources.Count}");
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync(cancellationToken.IsCancellationRequested ? "RAG: операция отменена." : "RAG: время ожидания API истекло.");
            return 1;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException
            or HttpRequestException or JsonException or System.Xml.XmlException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
        {
            await error.WriteLineAsync(e is HttpRequestException ? "RAG: не удалось связаться с OpenAI." : "RAG: " + e.Message);
            return 1;
        }
    }

    internal static async Task WriteVerboseAsync(TextWriter output, RagAnswer answer)
    {
        if (answer.NoRag)
            await output.WriteLineAsync("NO-RAG: поиск чанков не выполнялся.");
        else
        {
            for (var question = 0; question < answer.RetrievedQuestions.Count; question++)
            {
                var group = answer.RetrievedQuestions[question];
                await output.WriteLineAsync($"Вопрос {question + 1}: {group.Question}");
                var chosen = group.Context.Where(h => h.Selected).ToArray();
                await output.WriteLineAsync($"Проверено в индексе: {group.ScannedCount}; кандидатов рассмотрено: {group.Context.Count}; выбрано: {chosen.Length}.");
                if (chosen.Length == 0) await output.WriteLineAsync("Релевантные чанки не найдены.");
                for (var rank = 0; rank < chosen.Length; rank++)
                {
                    var hit = chosen[rank];
                    var length = Math.Min(400, hit.Chunk.Text.Length);
                    if (length < hit.Chunk.Text.Length && char.IsHighSurrogate(hit.Chunk.Text[length - 1])) length--;
                    var excerpt = hit.Chunk.Text[..length] + (length < hit.Chunk.Text.Length ? "…" : "");
                    await output.WriteLineAsync($"rank: {rank + 1}; chunk_id: {hit.Chunk.ChunkId}");
                    await output.WriteLineAsync($"section: {hit.Chunk.Section}");
                    await output.WriteLineAsync($"similarity: {hit.SimilarityScore.ToString("F6", CultureInfo.InvariantCulture)}");
                    await output.WriteLineAsync($"rerank score: {hit.RerankScore?.ToString("F6", CultureInfo.InvariantCulture) ?? "n/a"}; final rank: {rank + 1}");
                    await output.WriteLineAsync($"exact family: {hit.ExactFamilyMatch}; exact terms: {string.Join(", ", hit.ExactTerms)}");
                    await output.WriteLineAsync($"owner: {hit.Chunk.OwnerFamilyId}; dense_rank={hit.DenseRank}; lexical_rank={hit.LexicalRank}; lexical score: {hit.LexicalScore:F6}; mode: {hit.RerankerMode}; decision: {hit.SelectionReason}");
                    foreach (var warning in hit.Chunk.Warnings) await output.WriteLineAsync("warning: " + warning);
                    await output.WriteLineAsync($"text: {excerpt}");
                    if (hit.MatchedPassage != null) await output.WriteLineAsync("matched passage: " + hit.MatchedPassage);
                    await output.WriteLineAsync();
                }
                foreach (var hit in group.Context.Where(h => !h.Selected))
                    await output.WriteLineAsync($"excluded: {hit.Chunk.ChunkId}; reason: {hit.SelectionReason}; section: {hit.Chunk.Section}");
            }
        }
        await output.WriteLineAsync("Ответ:");
    }

    private static object Diagnostic(RagAnswer answer) => new
    {
        answer = answer.Text, mode = answer.NoRag ? "no-rag" : "rag",
        sources = answer.Sources.Select((h, i) => new { h.Chunk.Source, h.Chunk.Section, h.Chunk.ChunkId, h.Chunk.Strategy, h.SimilarityScore, h.RerankScore, final_rank = i + 1 }),
        questions = answer.RetrievedQuestions.Select(q => new
        {
            question = q.Question, scanned = q.ScannedCount,
            candidates = q.Context.Select((h, i) => new { h.Chunk.ChunkId, h.Chunk.Section, h.Chunk.OwnerFamilyId,
                h.SimilarityScore, h.LexicalScore, h.FusionScore, h.DenseRank, h.LexicalRank, h.RerankScore, h.RerankerMode,
                h.ExactFamilyMatch, h.ExactTerms, h.Selected, h.SelectionReason, h.MatchedPassage,
                fragment = h.Selected ? ContextAssembler.Fragment(h) : null, candidate_order = i + 1 })
        })
    };
}
