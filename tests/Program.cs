using System.Net;
using System.Reflection;
using System.Text.Json;
using BimSAgentApp;

var originalDirectory = Environment.CurrentDirectory;
var toolingTemp = @"D:\BIM-S_TestArtifacts\tooling-temp";
Directory.CreateDirectory(toolingTemp);
Environment.SetEnvironmentVariable("TEMP", toolingTemp);
Environment.SetEnvironmentVariable("TMP", toolingTemp);
var testRoot = Path.Combine(@"D:\BIM-S_TestArtifacts", "BimSAgent", "runs", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
Environment.CurrentDirectory = testRoot;
Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key-no-network");
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    passed++;
    Console.WriteLine("PASS " + name);
}

try
{
    ContextTests.Run(Check, testRoot);
    using var agent = new BimSAgent(Path.Combine(testRoot, "profiles"));
    var fake = new FakeHttp();
    var httpField = typeof(BimSAgent).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
    ((HttpClient)httpField.GetValue(agent)!).Dispose();
    httpField.SetValue(agent, new HttpClient(fake));
    var preparations = 0;
    Task Prepare(CancellationToken _) { preparations++; McpCommands.Events.Add("facts"); return Task.CompletedTask; }

    foreach (var tools in new[]
    {
        new[] { "get-model" },
        new[] { "get-model", "compare-model-versions" },
        new[] { "get-documentation", "compare-model-versions" },
        new[] { "get-model", "get-documentation", "compare-model-versions" }
    })
    {
        fake.Plan = Plan(tools);
        McpCommands.Events.Clear();
        await agent.AskWithMcpAsync("fixture question", 500, 0, prepareNonMcp: Prepare);
        Check(McpCommands.Events.SequenceEqual(new[] { "catalog", "planner" }.Concat(tools).Append("answer")),
            "one planner + ordered MCP steps + one final: " + string.Join(",", tools));
        Check(agent.LastResponseUsedMcp && preparations == 0, "MCP skips facts preparation");
        Check(agent.LastMcpContextSize is { AfterUtf8Bytes: <= McpFinalContext.MaxBytes }, "final MCP context obeys byte budget");
        Check(fake.LastAnswerInput.Contains("123") && fake.LastAnswerInput.Contains("thickness"), "final sees structured data");
        Check(fake.LastInstructions.Contains("semanticChanges") && fake.LastInstructions.Contains("{old} → {new}"),
            "final prompt requires semantic changes and exact old/new pairs");
        if (tools.Contains("compare-model-versions"))
            Check(fake.LastAnswerInput.Contains("semanticChanges") && !fake.LastAnswerInput.Contains("rawValue"),
                "semantic MCP3 payload reaches final LLM without raw differences");
        Check(!fake.LastInstructions.Contains("Ты не подключен к Revit"), "MCP instructions allow observed Revit data");
        var before = fake.Completions;
        await agent.UpdateMemoryAsync();
        Check(fake.Completions == before, "MCP does not enqueue automatic memory classification");
    }

    McpCommands.FailTool = "get-model";
    fake.Plan = Plan(["get-model", "compare-model-versions"]);
    McpCommands.Events.Clear();
    await agent.AskWithMcpAsync("fixture failure", 500, 0, prepareNonMcp: Prepare);
    Check(McpCommands.Events.SequenceEqual(new[] { "catalog", "planner", "get-model", "answer" }), "upstream MCP error stops dependent comparison");
    McpCommands.FailTool = null;

    fake.Plan = Plan(["get-model", "compare-model-versions"], badArguments: true);
    McpCommands.Events.Clear();
    try { await agent.AskWithMcpAsync("bad plan", 500, 0); throw new Exception("Invalid plan accepted."); }
    catch (InvalidOperationException) { Check(McpCommands.Events.SequenceEqual(new[] { "catalog", "planner" }), "all arguments validated before any MCP step"); }

    fake.Plan = Plan([]);
    McpCommands.Events.Clear();
    await agent.AskWithMcpAsync("ordinary question", 500, 0, prepareNonMcp: Prepare);
    Check(!agent.LastResponseUsedMcp && preparations == 1, "ordinary route keeps facts preparation");
    Check(McpCommands.Events.SequenceEqual(new[] { "catalog", "planner", "facts", "answer", "invariant_check" }), "ordinary route keeps invariant reviewer");
    await agent.UpdateMemoryAsync();
    Check(McpCommands.Events[^1] == "memory_changes", "ordinary route keeps memory classifier");
    McpCommands.Events.Clear();
    await agent.AskAsync("direct ordinary request", 500, 0);
    Check(McpCommands.Events.SequenceEqual(new[] { "answer", "invariant_check" }), "direct AskAsync remains unchanged");
    Check(!agent.LastResponseUsedMcp, "single-pass flag does not leak to ordinary responses");
    Check(!File.ReadAllText(Path.Combine(testRoot, "history.json")).Contains("MCP результаты"), "MCP context is not persisted in dialogue history");
    Console.WriteLine($"{passed} checks passed. No live LLM or Revit calls.");
}
finally { Environment.CurrentDirectory = originalDirectory; }

static string Plan(string[] tools, bool badArguments = false) => JsonSerializer.Serialize(new
{
    useMcp = tools.Length > 0,
    steps = tools.Select(tool => new
    {
        server = tool == "get-model" ? "mcp1" : tool == "get-documentation" ? "mcp2" : "mcp3",
        tool,
        argumentsJson = tool == "compare-model-versions"
            ? badArguments ? "[]" : "{\"version1\":\"previous\",\"version2\":\"latest\",\"mode\":\"both\"}"
            : "{}"
    }),
    reason = "fixture"
});

sealed class FakeHttp : HttpMessageHandler
{
    public string Plan { get; set; } = "";
    public int Completions { get; private set; }
    public string LastAnswerInput { get; private set; } = "";
    public string LastInstructions { get; private set; } = "";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/input_tokens")) return Json(new { input_tokens = 10 });
        if (request.RequestUri.AbsolutePath != "/v1/responses") throw new Exception("Unexpected HTTP request.");
        Completions++;
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var root = body.RootElement;
        var name = root.TryGetProperty("text", out var text) && text.GetProperty("format").TryGetProperty("name", out var n)
            ? n.GetString() : null;
        var answer = name switch
        {
            "mcp_plan" => Plan,
            "invariant_check" => "{\"violations\":[]}",
            "memory_changes" => "{\"changes\":[]}",
            _ => "fixture answer"
        };
        McpCommands.Events.Add(name == "mcp_plan" ? "planner" : name ?? "answer");
        if (name == null)
        {
            LastAnswerInput = root.GetProperty("input").ToString();
            LastInstructions = root.GetProperty("instructions").GetString()!;
        }
        return Json(new
        {
            output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = answer } } } },
            usage = new { input_tokens = 10, output_tokens = 10 }
        });
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value)) };
}
