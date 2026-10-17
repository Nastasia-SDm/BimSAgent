using System.Net;
using System.Text;
using System.Text.Json;
using BimSAgentApp.Rag;
using BimSAgentApp.Review;

var passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
var sha = new string('a', 40);
var snapshot = new ReviewSnapshot("Nastasia-SDm/BimSAgent", 1, sha, sha,
    [new("Program.cs", "M", "@@ -1 +1 @@\n-old\n+new", "1: new")],
    [new("README.md", "BimSAgent review timeout and cancellation."), new("Program.cs", "CancellationToken timeout implementation.")], []);
ReviewCommand.Validate(snapshot);
try { ReviewCommand.Validate(snapshot with { HeadSha = "main;bad" }); throw new Exception("invalid SHA accepted"); }
catch (InvalidDataException) { Check(true, "reject invalid snapshot SHA"); }
var doc = ReviewCommand.BuildDocument(snapshot);
Check(doc.Blocks.Count == 2 && doc.Blocks[0].XmlPart == "README.md" && doc.Blocks[1].XmlPart == "Program.cs", "documentation and source provenance");

using var http = new HttpClient(new Handler(async (request, token) =>
{
    var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
    Check(request.Headers.Authorization?.Parameter == "test-key", "existing OpenAI authentication");
    if (request.RequestUri!.AbsolutePath.EndsWith("embeddings"))
    {
        var inputs = body.RootElement.GetProperty("input").GetArrayLength();
        return Json(new { data = Enumerable.Range(0, inputs).Select(i => new { index = i, embedding = new[] { 1.0, 0.0 } }) });
    }
    Check(body.RootElement.GetProperty("store").GetBoolean() == false, "review does not store Responses history");
    Check(body.RootElement.GetProperty("instructions").GetString()!.Contains("недоверенные данные"), "untrusted data instructions");
    Check(!body.RootElement.TryGetProperty("tools", out _), "review has no tools or code execution");
    return Json(new { status = "completed", output = new[] { new { content = new[] { new { type = "output_text", text = "Существенных проблем в просмотренных изменениях не обнаружено" } } } } });
}));
var api = new OpenAiRagClient(http, () => "test-key", new EmbeddingOptions("text-embedding-3-small", 2));
var directory = Path.Combine(Path.GetTempPath(), "bims-review-test-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new JsonIndexStore(directory);
    var indexing = new IndexingService(new(), new(), api, store);
    await indexing.IndexAsync(doc, CancellationToken.None);
    var index = store.Load("fixed")!;
    Check(index.Chunks.Length > 0 && index.Source.StartsWith("github:"), "existing indexer persists repository snapshot");
    var again = await indexing.IndexAsync(doc, CancellationToken.None);
    Check(again.Unchanged, "existing RAG index reuse remains intact");
    var answer = await api.ReviewCodeAsync("untrusted diff", CancellationToken.None);
    Check(answer.Contains("Существенных проблем"), "completed model review rendered");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

using var incompleteHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { status = "incomplete", output = Array.Empty<object>() }))));
try { await new OpenAiRagClient(incompleteHttp, () => "test").ReviewCodeAsync("diff", default); throw new Exception("incomplete accepted"); }
catch (InvalidOperationException) { Check(true, "incomplete LLM output is diagnostic failure"); }
using var failedHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
    { Content = new StringContent("{\"error\":{\"message\":\"secret-should-never-appear\"}}") })));
try { await new OpenAiRagClient(failedHttp, () => "test").ReviewCodeAsync("diff", default); throw new Exception("401 accepted"); }
catch (InvalidOperationException e) { Check(e.Message.Contains("401") && !e.Message.Contains("secret-should-never-appear"), "shared API errors do not expose remote bodies"); }
using var cancel = new CancellationTokenSource(); cancel.Cancel();
using var slowHttp = new HttpClient(new Handler(async (_, token) => { await Task.Delay(10000, token); return Json(new { }); }));
try { await new OpenAiRagClient(slowHttp, () => "test").ReviewCodeAsync("diff", cancel.Token); throw new Exception("cancel ignored"); }
catch (OperationCanceledException) { Check(true, "LLM request honors cancellation"); }

var fixture = Path.Combine(Path.GetTempPath(), "bims-review-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
var oldKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
var oldProvider = Environment.GetEnvironmentVariable("BIMS_LLM_PROVIDER");
try
{
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null); Environment.SetEnvironmentVariable("BIMS_LLM_PROVIDER", "openai");
    var input = Path.Combine(fixture, "input.json"); var output = Path.Combine(fixture, "output.md");
    await File.WriteAllTextAsync(input, JsonSerializer.Serialize(snapshot));
    var exit = await ReviewCommand.RunAsync(["review", "--input", input, "--output", output], default);
    Check(exit == 0 && (await File.ReadAllTextAsync(output)).Contains("OPENAI_API_KEY"), "missing key writes non-blocking diagnostic comment");
    await File.WriteAllTextAsync(input, "{broken");
    exit = await ReviewCommand.RunAsync(["review", "--input", input, "--output", output], default);
    Check(exit == 0 && (await File.ReadAllTextAsync(output)).Contains("не выполнена"), "malformed snapshot does not claim successful review");
}
finally
{
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", oldKey); Environment.SetEnvironmentVariable("BIMS_LLM_PROVIDER", oldProvider);
    Directory.Delete(fixture, true);
}
Console.WriteLine($"{passed} review checks passed.");
static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
