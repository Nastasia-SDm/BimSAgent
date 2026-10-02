using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BimSAgentApp.Rag;

public static class RagCommands
{
    public const string Help = "rag index \"<docx>\" | rag status | rag compare \"<вопрос>\" | rag ask \"<вопрос>\" [--strategy fixed|structural | --no-rag] [--top-k 5] [--json]";

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
        IEmbeddingClient? embeddingClient = null, IRagAnswerGenerator? answerGenerator = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        try
        {
            if (args.Length < 2 || !args[0].Equals("rag", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(Help);
            var command = args[1].ToLowerInvariant();
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
            var topK = 5;
            for (var i = 3; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--strategy" when command == "ask" && i + 1 < args.Length:
                        strategyOption = args[++i]; strategySpecified = true; RagDefaults.ValidateStrategy(strategyOption); break;
                    case "--no-rag" when command == "ask": noRag = true; break;
                    case "--top-k" when command != "index" && i + 1 < args.Length:
                        if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out topK) || topK is < 1 or > 100)
                            throw new ArgumentException("top-K должен быть от 1 до 100.");
                        break;
                    case "--json" when command != "index": json = true; break;
                    default: throw new ArgumentException("Неизвестный или неполный параметр: " + args[i]);
                }
            }
            if (noRag && strategySpecified) throw new ArgumentException("--no-rag не совмещается с --strategy.");
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
            var api = new OpenAiRagClient(http);
            var embeddings = embeddingClient ?? api;
            var tokenizer = new RagTokenizer();
            if (command == "index")
            {
                var indexing = new IndexingService(new(), tokenizer, embeddings, store);
                var counts = await indexing.IndexAsync(args[2], cancellationToken);
                await output.WriteLineAsync($"{(counts.Unchanged ? "Документ не изменился" : "Индексация завершена")}: fixed={counts.Fixed}, structural={counts.Structural}.");
                return 0;
            }
            var service = new RagService(embeddings, answerGenerator ?? api, store, new CosineRetriever(), tokenizer);
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
                var result = await service.AskAsync(args[2], strategyOption, noRag, topK, cancellationToken: cancellationToken);
                await output.WriteLineAsync(json ? JsonSerializer.Serialize(Diagnostic(result), JsonIndexStore.JsonOptions) : result.Text);
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

    private static object Diagnostic(RagAnswer answer) => new
    {
        answer = answer.Text, mode = answer.NoRag ? "no-rag" : "rag",
        sources = answer.Sources.Select(h => new { h.Chunk.Source, h.Chunk.Section, h.Chunk.ChunkId, h.Chunk.Strategy, h.SimilarityScore })
    };
}
