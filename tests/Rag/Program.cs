using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BimSAgentApp.Rag;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

Console.OutputEncoding = Encoding.UTF8;
var root = Path.Combine(@"D:\BIM-S_TestArtifacts\rag-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("TEMP", root);
Environment.SetEnvironmentVariable("TMP", root);
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}
async Task Throws(Func<Task> action, string name)
{
    try { await action(); }
    catch (Exception e) when (e is InvalidDataException or ArgumentException or InvalidOperationException)
    { Check(true, name); return; }
    throw new Exception("FAIL: " + name);
}

const string landing = "КркНес_ЛПлощадка_Монолитная";
const string beam = "КркНес_Балка_Монолитная";
var fixture = Path.Combine(root, "Семейства с пробелами.docx");
WriteDocument(fixture);
var extractor = new DocxDocumentExtractor();
var document = extractor.Extract(fixture);
Check(document.Blocks.Count == 8, "DOCX paragraphs and table rows in document order");
Check(document.Blocks[0].Text == landing && document.Blocks[1].FamilyNames.Contains(landing), "Russian family name and inherited heading");
Check(document.Blocks[0].Kind == "heading", "custom heading style inheritance");
Check(document.Blocks.Any(b => b.Kind.StartsWith("table-row") && b.Text.Contains("назначение балки")), "table cell relationships preserved");
Check(document.Blocks.Single(b => b.Text.Contains("назначение балки")).FamilyNames.SequenceEqual(new[] { beam }), "family identified in labelled table column");
Check(!document.Blocks.SelectMany(b => b.FamilyNames).Contains("Балка_Высота"), "parameter name is not mistaken for family");
Check(!document.Blocks.Any(b => b.Text.Contains("удалённый текст")), "deleted text excluded");

var tokenizer = new RagTokenizer();
var longText = string.Concat(Enumerable.Repeat("Русская лестничная площадка 🏗️ и арматура. ", 500));
var ranges = tokenizer.Windows(longText, 600, 100).ToArray();
Check(ranges.Length > 2 && ranges[0].Start == 0 && ranges[^1].End == longText.Length, "fixed windows cover long text");
Check(ranges.All(r => tokenizer.Count(longText[r.Start..r.End]) <= 600), "fixed windows obey token limit");
Check(ranges.Zip(ranges.Skip(1)).All(p => p.Second.Start < p.First.End && p.Second.Start > p.First.Start), "overlap and forward progress");
Check(ranges.All(r => !char.IsLowSurrogate(longText[r.Start]) && !char.IsHighSurrogate(longText[r.End - 1])), "Unicode boundaries intact");
var largeDoc = document with { Blocks = new[] { new DocumentBlock(1, landing, longText, "paragraph", [landing]) } };
var fixedChunks = new FixedWindowChunker(tokenizer).Split(largeDoc);
Check(fixedChunks.Count == ranges.Length && fixedChunks.All(c => c.FamilyNames.Contains(landing)), "fixed chunk metadata");
Check(fixedChunks.Select(c => c.ChunkId).SequenceEqual(new FixedWindowChunker(tokenizer).Split(largeDoc).Select(c => c.ChunkId)), "deterministic chunk IDs");
var structural = new StructuralChunker(tokenizer).Split(document);
Check(structural.Count > 1 && structural.All(c => c.TokenCount <= 2000), "structural chunking");
Check(structural.All(c => !(c.Text.Contains(landing) && c.Text.Contains(beam))), "different family descriptions separated");
var longStructural = new StructuralChunker(tokenizer).Split(largeDoc);
Check(longStructural.Count > 1 && longStructural.All(c => c.TokenCount <= 2000), "oversized family description splits safely");
Check(Math.Abs(CosineRetriever.Similarity([1, 0], [1, 0]) - 1) < 1e-9, "cosine identical vectors");
Check(CosineRetriever.Similarity([1, 0], [0, 1]) == 0, "cosine orthogonal vectors");
Check(CosineRetriever.Similarity([1, 0], [-1, 0]) == -1, "cosine opposite vectors");
await Throws(() => { CosineRetriever.Similarity([0, 0], [1, 0]); return Task.CompletedTask; }, "reject zero vector");

var directory = Path.Combine(root, "indexes");
var store = new JsonIndexStore(directory);
var embedder = new FakeEmbeddings();
var generator = new FakeGenerator();
var indexing = new IndexingService(extractor, tokenizer, embedder, store);
var counts = await indexing.IndexAsync(fixture, default);
Check(counts.Fixed > 0 && counts.Structural > 0 && generator.Requests.Count == 0, "both indexes created without generation");
var first = store.Load("fixed")!;
var second = store.Load("structural")!;
Check(first.BuildId == second.BuildId && first.Chunks.All(c => c.Embedding.Length == 1536), "paired independent persisted embeddings");
Check(File.ReadAllText(Path.Combine(directory, "fixed.json")).Contains("chunk_id"), "snake_case metadata persisted");
var embeddingCalls = embedder.Calls;
Check((await indexing.IndexAsync(fixture, default)).Unchanged && embedder.Calls == embeddingCalls, "unchanged document makes no embedding calls");

var synthetic = first with { Chunks = new[]
{
    first.Chunks[0] with { ChunkId = "b", Embedding = FakeEmbeddings.Vector(0, 1) },
    first.Chunks[0] with { ChunkId = "a", Embedding = FakeEmbeddings.Vector(1, 0) },
    first.Chunks[0] with { ChunkId = "c", Embedding = FakeEmbeddings.Vector(-1, 0) }
} };
var hits = new CosineRetriever().Search(synthetic, FakeEmbeddings.Vector(1, 0), 2);
Check(hits.Count == 2 && hits[0].Chunk.ChunkId == "a" && hits[0].SimilarityScore == 1 && hits[1].SimilarityScore == 0, "top-K and stored scores");
var service = new RagService(embedder, generator, store, new CosineRetriever(), tokenizer);
var comparison = await service.CompareAsync("Какая лестничная площадка?", 2);
Check(embedder.Calls == embeddingCalls + 1, "comparison embeds question only once");
Check(generator.Requests.Count == 2 && generator.Requests[0].Question == generator.Requests[1].Question
    && generator.Requests[0].Options == generator.Requests[1].Options, "same question and settings for comparison");
Check(generator.Requests[0].Context.All(h => h.Chunk.Strategy == "fixed")
    && generator.Requests[1].Context.All(h => h.Chunk.Strategy == "structural")
    && generator.Requests.All(r => r.Context.Count <= 2), "separate indexes and same top-K");
Check(comparison.Fixed.Sources.Count > 0 && !comparison.Fixed.NoRag, "RAG returns provenance");
embeddingCalls = embedder.Calls;
var noRag = await new RagService(embedder, generator, new JsonIndexStore(Path.Combine(root, "missing")), new CosineRetriever(), tokenizer)
    .AskAsync("Что такое площадка?", noRag: true);
Check(noRag.NoRag && embedder.Calls == embeddingCalls && generator.Requests[^1].Context.Count == 0, "NO-RAG needs no index or embeddings");
var emptyService = new RagService(embedder, generator, store, new EmptyRetriever(), tokenizer);
var generations = generator.Requests.Count;
Check((await emptyService.AskAsync("Неизвестное")).Text == RagDefaults.Unknown && generator.Requests.Count == generations,
    "no context produces unknown without generation");

var output = new StringWriter();
var error = new StringWriter();
async Task<int> Command(params string[] command)
{
    output.GetStringBuilder().Clear(); error.GetStringBuilder().Clear();
    return await RagCommands.RunAsync(command, default, output, error, directory, embedder, generator);
}
Check(RagCommands.Split("rag index \"D:\\BIM-база\\Семейства Revit.docx\"")[2] == "D:\\BIM-база\\Семейства Revit.docx", "CLI preserves Windows path and spaces");
Check(await Command("rag", "index", fixture) == 0, "CLI index");
Check(await Command("rag", "status") == 0 && output.ToString().Contains("structural:"), "CLI status");
Check(await Command("rag", "compare", "площадка") == 0
    && output.ToString() == "Фиксированное разбиение:" + Environment.NewLine + "Ответ из документа." + Environment.NewLine
    + Environment.NewLine + "Структурное разбиение:" + Environment.NewLine + "Ответ из документа." + Environment.NewLine,
    "CLI compare exact short format");
Check(await Command("rag", "ask", "площадка", "--strategy", "fixed") == 0, "CLI ask fixed");
Check(await Command("rag", "ask", "площадка", "--strategy", "structural") == 0, "CLI ask structural");
Check(await Command("rag", "ask", "площадка", "--no-rag") == 0 && output.ToString().Contains("Без справочника"), "CLI NO-RAG");
Check(await Command("rag", "ask", "площадка", "--json") == 0 && output.ToString().Contains("similarity_score")
    && !output.ToString().Contains("embedding"), "CLI diagnostics expose scores without vectors");
Check(await Command("rag", "ask", "площадка", "--top-k", "0") != 0, "CLI rejects invalid top-K");
Check(await Command("rag", "ask", "площадка", "--no-rag", "--strategy", "fixed") != 0, "CLI rejects conflicting modes");
Check(!Directory.EnumerateFiles(root, "history.json", SearchOption.AllDirectories).Any(), "RAG never creates dialogue history");

var startInfo = new ProcessStartInfo("dotnet")
{
    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
    RedirectStandardOutput = true, RedirectStandardError = true
};
startInfo.ArgumentList.Add(typeof(RagService).Assembly.Location);
startInfo.ArgumentList.Add("rag");
startInfo.ArgumentList.Add("help"); // Do not depend on the user's installed index version.
startInfo.Environment["OPENAI_API_KEY"] = "";
using (var child = Process.Start(startInfo)!)
{
    var stdout = child.StandardOutput.ReadToEndAsync();
    var stderr = child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync();
    Check(child.ExitCode == 0 && (await stdout).Contains("rag index") && string.IsNullOrWhiteSpace(await stderr), "actual Program.cs argument route works without key");
}
Check(!File.Exists(Path.Combine(root, "facts.json")) && !File.Exists(Path.Combine(root, "history.json")), "actual CLI bypasses stateful BimSAgent constructor");

WriteDocument(fixture, "Изменённое описание.");
var before = File.ReadAllBytes(Path.Combine(directory, "fixed.json"));
await Throws(() => new IndexingService(extractor, tokenizer, new FailingEmbeddings(), store).IndexAsync(fixture, default), "embedding failure reported");
Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "fixed.json"))), "embedding failure preserves existing index");
var updated = await indexing.IndexAsync(fixture, default);
Check(!updated.Unchanged && store.Load("fixed")!.ContentHash != first.ContentHash, "changed DOCX reindexed");
await Throws(() => { RagService.ValidatePair(first, store.Load("structural")!); return Task.CompletedTask; }, "mismatched index pair rejected");

using var handler = new RecordingHttp();
using var http = new HttpClient(handler);
var api = new OpenAiRagClient(http, () => "test-key-no-network");
var vectors = await api.EmbedAsync(["один", "два"], default);
Check(vectors[0][0] == 1 && vectors[1][1] == 1, "HTTP embeddings response mapped by index");
Check(handler.Payloads[0].GetProperty("model").GetString() == "text-embedding-3-small", "HTTP embedding model");
var options = new AnswerOptions();
await api.GenerateAsync(new("Вопрос", comparison.Fixed.Sources, false, options), default);
await api.GenerateAsync(new("Вопрос", [], true, options), default);
var ragPayload = handler.Payloads[1];
var baseline = handler.Payloads[2];
Check(ragPayload.GetProperty("model").GetString() == RagDefaults.AnswerModel
    && baseline.GetProperty("model").GetString() == RagDefaults.AnswerModel
    && ragPayload.GetProperty("temperature").GetDouble() == baseline.GetProperty("temperature").GetDouble()
    && ragPayload.GetProperty("max_output_tokens").GetInt32() == baseline.GetProperty("max_output_tokens").GetInt32(), "HTTP same generation settings");
using var baselineInput = JsonDocument.Parse(baseline.GetProperty("input").GetString()!);
Check(baselineInput.RootElement.GetProperty("fragments").GetArrayLength() == 0, "HTTP NO-RAG sends no fragments");
Check(!ragPayload.GetProperty("store").GetBoolean() && ragPayload.GetProperty("instructions").GetString()!.Contains("только на основании"), "HTTP grounded prompt and store false");
handler.Fail = true;
await Throws(() => api.EmbedAsync(["test"], default), "HTTP authentication error handled");

await MultiQuestionTests.Run(Check, store, tokenizer);
await RetrievalQualityTests.Run(Check, store.Load("structural")!, tokenizer, fixture, root);
await FamilyPipelineTests.Run(Check, store.Load("structural")!);

Check(await Command("rag", "ask", "площадка", "--strategy", "structural") == 0, "verbose baseline ask");
var ordinaryOutput = output.ToString();
var ordinaryRequest = JsonSerializer.Serialize(generator.Requests[^1]);
var callsBeforeVerbose = embedder.Calls;
var generationsBeforeVerbose = generator.Requests.Count;
Check(await Command("rag", "ask", "площадка", "--strategy", "structural", "--verbose") == 0,
    "CLI accepts ask --verbose");
var verboseOutput = output.ToString();
Check(verboseOutput.Contains("rank: 1; chunk_id:") && verboseOutput.Contains("section:")
    && verboseOutput.Contains("similarity:") && verboseOutput.Contains("text:")
    && verboseOutput.Contains("rerank score:") && verboseOutput.Contains("final rank:")
    && verboseOutput.EndsWith("Ответ:" + Environment.NewLine + ordinaryOutput), "verbose diagnostics precede unchanged answer");
Check(embedder.Calls == callsBeforeVerbose + 1 && generator.Requests.Count == generationsBeforeVerbose + 1
    && JsonSerializer.Serialize(generator.Requests[^1]) == ordinaryRequest, "verbose preserves generation request and call counts");
callsBeforeVerbose = embedder.Calls;
generationsBeforeVerbose = generator.Requests.Count;
Check(await Command("rag", "ask", "1. Площадка? 2. Балка?", "--strategy", "structural", "--top-k", "1", "--verbose") == 0,
    "CLI verbose numbered questions");
Check(output.ToString().Contains("Вопрос 1: Площадка?") && output.ToString().Contains("Вопрос 2: Балка?")
    && output.ToString().Split("rank: 1;").Length == 3
    && embedder.Calls == callsBeforeVerbose + 2 && generator.Requests.Count == generationsBeforeVerbose + 1,
    "verbose per-question ranking with two embeddings and one generation");
callsBeforeVerbose = embedder.Calls;
Check(await Command("rag", "ask", "площадка", "--no-rag", "--verbose") == 0
    && output.ToString().Contains("поиск чанков не выполнялся") && embedder.Calls == callsBeforeVerbose,
    "verbose NO-RAG performs no retrieval");
Check(await Command("rag", "ask", "площадка", "--verbose", "--json") != 0, "verbose cannot corrupt JSON output");
Check(await Command("rag", "compare", "площадка", "--verbose") != 0, "verbose scoped to ask only");

if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
{
    var real = extractor.Extract(args[0]);
    var realFixed = new FixedWindowChunker(tokenizer).Split(real);
    var realStructural = new StructuralChunker(tokenizer).Split(real);
    Check(real.Families.Any(f => f.CanonicalName == "IFC_Набор_Балка_№2"), "real numbered family with explanatory heading identified");
    Check(real.Families.Any(f => f.CanonicalName == "КркНес_ЛМарш_Сборный" && f.Status == "conflict"), "real source copy-paste conflict detected");
    Check(real.Assets.Length > 1000 && real.Assets.All(a => a.XmlPath.Length > 0), "real image occurrences retain source anchors");
    Check(real.Blocks.Where(b => b.Cells.Length > 0).All(b => b.TableId != null && b.TableHeaders.Length > 0), "real table rows retain table identities and headers");
    Check(real.Blocks.Count > 0 && real.Blocks.Any(b => b.Kind.StartsWith("table-row")), "real DOCX extracted with tables");
    Check(realFixed.Count > 0 && realStructural.Count > 0 && realFixed.All(c => c.TokenCount <= 600)
        && realStructural.All(c => c.TokenCount <= 2000), "real DOCX both chunkers obey limits");
    Check(realFixed.SelectMany(c => c.BlockOrdinals).Distinct().Count() == real.Blocks.Count
        && realStructural.SelectMany(c => c.BlockOrdinals).Distinct().Count() == real.Blocks.Count, "real DOCX all blocks covered by both indexes");
    Console.WriteLine($"REAL DOCUMENT: {real.Blocks.Count} blocks, {realFixed.Count} fixed, {realStructural.Count} structural; no OpenAI calls.");
}
Console.WriteLine($"{passed} RAG checks passed. Artifacts: {root}. No live API calls.");

static void WriteDocument(string path, string extra = "")
{
    using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
    var main = doc.AddMainDocumentPart();
    var styles = main.AddNewPart<StyleDefinitionsPart>();
    styles.Styles = new Styles(
        new Style(new StyleName { Val = "Heading 1" }, new StyleParagraphProperties(new OutlineLevel { Val = 0 })) { StyleId = "base", Type = StyleValues.Paragraph },
        new Style(new BasedOn { Val = "base" }) { StyleId = "custom", Type = StyleValues.Paragraph });
    Paragraph Para(string text) => new(new Run(new Text(text)));
    Paragraph Heading(string text) => new(new ParagraphProperties(new ParagraphStyleId { Val = "custom" }), new Run(new Text(text)));
    main.Document = new Document(new Body(
        Heading("КркНес_ЛПлощадка_Монолитная"),
        Para("Лестничная площадка. Монолитная конструкция. " + extra),
        new Table(new TableRow(new TableCell(Para("Параметр")), new TableCell(Para("Значение"))),
            new TableRow(new TableCell(Para("Балка_Высота")), new TableCell(Para("100")))),
        Heading("Балки"),
        Para("Сведения о балках."),
        new Table(new TableRow(new TableCell(Para("Название семейства")), new TableCell(Para("Описание"))),
            new TableRow(new TableCell(Para("КркНес_Балка_Монолитная")), new TableCell(Para("назначение балки")))),
        new Paragraph(new DeletedRun(new DeletedText("удалённый текст")))));
}

sealed class FakeEmbeddings : IEmbeddingClient
{
    public int Calls { get; private set; }
    public static float[] Vector(float first, float second)
    {
        var result = new float[1536]; result[0] = first; result[1] = second; return result;
    }
    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(texts.Select(t => t.Contains("площад", StringComparison.OrdinalIgnoreCase) ? Vector(1, 0) : Vector(0, 1)).ToArray());
    }
}
sealed class FailingEmbeddings : IEmbeddingClient
{
    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) => throw new InvalidOperationException("fixture failure");
}
sealed class FakeGenerator : IRagAnswerGenerator
{
    public List<RagAnswerRequest> Requests { get; } = [];
    public Task<string> GenerateAsync(RagAnswerRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(request.NoRag ? "Без справочника." : "Ответ из документа.");
    }
}
sealed class EmptyRetriever : IRetriever
{
    public IReadOnlyList<RetrievalHit> Search(RagIndex index, float[] query, int topK) => [];
}
sealed class RecordingHttp : HttpMessageHandler
{
    public List<JsonElement> Payloads { get; } = [];
    public bool Fail { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        Payloads.Add(document.RootElement.Clone());
        if (Fail) return new(HttpStatusCode.Unauthorized) { Content = new StringContent("secret must not be displayed") };
        object payload = request.RequestUri!.AbsolutePath.EndsWith("embeddings")
            ? new { data = new[] { new { index = 1, embedding = FakeEmbeddings.Vector(0, 1) }, new { index = 0, embedding = FakeEmbeddings.Vector(1, 0) } } }
            : new { status = "completed", output = new[] { new { content = new[] { new { type = "output_text", text = "Ответ." } } } } };
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload)) };
    }
}
